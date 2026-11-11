#include "audiocpp.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include <fcntl.h>
#include <io.h>
#include <windows.h>

namespace {
constexpr int app_rate = 48000;
constexpr int model_rate = 16000;
constexpr size_t block_size = 7680;
constexpr size_t max_output = app_rate;

void check(audiocpp_status status) {
    if (status != AUDIOCPP_OK)
        throw std::runtime_error(audiocpp_last_error());
}

float finite_sample(float sample) {
    return std::isfinite(sample) ? std::clamp(sample, -1.0f, 1.0f) : 0.0f;
}

void resample(const float* input, size_t length, int from, float* output, size_t count, int to) {
    for (size_t i = 0; i < count; ++i) {
        const size_t offset = i * from;
        const size_t low = std::min(offset / to, length - 1);
        const size_t high = std::min(low + 1, length - 1);
        const float fraction = static_cast<float>(offset % to) / to;
        output[i] = input[low] + (input[high] - input[low]) * fraction;
    }
}

uint16_t u16(const unsigned char* bytes) {
    return uint16_t(bytes[0]) | (uint16_t(bytes[1]) << 8);
}

uint32_t u32(const unsigned char* bytes) {
    return uint32_t(bytes[0]) | (uint32_t(bytes[1]) << 8) |
        (uint32_t(bytes[2]) << 16) | (uint32_t(bytes[3]) << 24);
}

std::vector<float> read_reference(const std::filesystem::path& path) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    const auto length = file.tellg();
    if (length < 44 || length > 8 * 1024 * 1024)
        throw std::runtime_error("The selected reference must be a short PCM WAV.");
    std::vector<unsigned char> bytes(static_cast<size_t>(length));
    file.seekg(0);
    if (!file.read(reinterpret_cast<char*>(bytes.data()), length) ||
        std::string_view(reinterpret_cast<const char*>(bytes.data()), 4) != "RIFF" ||
        std::string_view(reinterpret_cast<const char*>(bytes.data() + 8), 4) != "WAVE")
        throw std::runtime_error("The selected reference is not a readable WAV.");

    uint32_t rate = 0;
    size_t data_offset = 0, data_length = 0;
    for (size_t chunk = 12; chunk + 8 <= bytes.size();) {
        const auto kind = std::string_view(reinterpret_cast<const char*>(bytes.data() + chunk), 4);
        const size_t count = u32(bytes.data() + chunk + 4);
        chunk += 8;
        if (count > bytes.size() - chunk)
            throw std::runtime_error("The selected reference contains a truncated WAV chunk.");
        if (kind == "fmt " && count >= 16) {
            const auto* fmt = bytes.data() + chunk;
            if (u16(fmt) != 1 || u16(fmt + 2) != 1 || u16(fmt + 14) != 16)
                throw std::runtime_error("The selected reference must be a 16-bit mono PCM WAV.");
            rate = u32(fmt + 4);
        } else if (kind == "data" && data_offset == 0) {
            data_offset = chunk;
            data_length = count;
        }
        chunk += count + (count & 1);
    }
    if (rate < 8000 || rate > 96000 || data_length / 2 < rate / 2)
        throw std::runtime_error("The selected reference is missing usable speech audio.");

    const size_t frames = std::min<size_t>(data_length / 2, rate * 10);
    std::vector<float> input(frames);
    for (size_t i = 0; i < frames; ++i)
        input[i] = static_cast<int16_t>(u16(bytes.data() + data_offset + i * 2)) / 32768.0f;
    std::vector<float> output(frames * model_rate / rate);
    resample(input.data(), input.size(), rate, output.data(), output.size(), model_rate);
    return output;
}

template<class T, void (*Release)(T*)>
using Handle = std::unique_ptr<T, decltype(Release)>;

class Converter {
public:
    Converter(const std::filesystem::path& model_path, const std::vector<float>& reference, const char* backend_name)
        : request_(audiocpp_request_create(), audiocpp_request_free) {
        if (!request_)
            throw std::runtime_error("Could not allocate the MeanVC2 stream request.");
        audiocpp_registry* registry = nullptr;
        check(audiocpp_registry_create(nullptr, &registry));
        registry_.reset(registry);

        const auto model_file = model_path.u8string();
        audiocpp_model* model = nullptr;
        const audiocpp_model_config selection{"meanvc2", nullptr, nullptr, "embedded"};
        check(audiocpp_model_load(registry_.get(), reinterpret_cast<const char*>(model_file.c_str()),
                                  &selection, nullptr, &model));
        model_.reset(model);

        audiocpp_session* session = nullptr;
        const audiocpp_backend_config backend{backend_name, 0, 4};
        check(audiocpp_session_create(model_.get(), "vc", "streaming", &backend, nullptr, &session));
        session_.reset(session);
        check(audiocpp_request_set_voice_audio(request_.get(), reference.data(), reference.size(), model_rate, 1));
        reset();
    }

    void reset() {
        check(audiocpp_stream_start(session_.get(), request_.get()));
        input_position_ = 0;
    }

    void run(std::array<float, block_size>& input, std::vector<float>& output) {
        for (auto& sample : input)
            sample = finite_sample(sample);
        output.clear();
        audiocpp_event* event = nullptr;
        check(audiocpp_stream_push(session_.get(), input.data(), input.size(), app_rate, 1,
                                   input_position_, &event));
        input_position_ += input.size();
        if (event)
            append(Handle<audiocpp_event, audiocpp_event_free>(event, audiocpp_event_free), output);
        while (true) {
            event = nullptr;
            check(audiocpp_stream_next_event(session_.get(), &event));
            if (!event)
                break;
            append(Handle<audiocpp_event, audiocpp_event_free>(event, audiocpp_event_free), output);
        }
    }

private:
    static void append(const Handle<audiocpp_event, audiocpp_event_free>& event, std::vector<float>& output) {
        const auto* result = audiocpp_event_as_result(event.get());
        if (!result)
            return;
        const float* samples = nullptr;
        size_t frames = 0;
        int rate = 0, channels = 0;
        auto status = audiocpp_result_audio(result, &samples, &frames, &rate, &channels);
        if (status == AUDIOCPP_ERR_NOT_AVAILABLE)
            return;
        check(status);
        if (frames == 0)
            return;
        if (!samples || rate != model_rate || channels != 1 || frames > (max_output - output.size()) / 3)
            throw std::runtime_error("MeanVC2 returned an unexpected audio block.");
        const size_t start = output.size();
        output.resize(start + frames * 3);
        resample(samples, frames, model_rate, output.data() + start, output.size() - start, app_rate);
        for (size_t i = start; i < output.size(); ++i)
            output[i] = finite_sample(output[i]);
    }

    Handle<audiocpp_registry, audiocpp_registry_free> registry_{nullptr, audiocpp_registry_free};
    Handle<audiocpp_model, audiocpp_model_free> model_{nullptr, audiocpp_model_free};
    Handle<audiocpp_session, audiocpp_session_free> session_{nullptr, audiocpp_session_free};
    Handle<audiocpp_request, audiocpp_request_free> request_;
    int64_t input_position_ = 0;
};
}

int wmain(int argc, wchar_t** argv) {
    try {
        const bool full_precision = argc == 5 && std::wstring_view(argv[3]) == L"--variant" &&
            std::wstring_view(argv[4]) == L"fp32";
        if ((argc != 3 && !full_precision) || std::wstring_view(argv[1]) != L"--reference")
            throw std::runtime_error("Expected --reference <profile.wav> and optional --variant fp32.");
        _setmode(_fileno(stdin), _O_BINARY);
        _setmode(_fileno(stdout), _O_BINARY);

        wchar_t executable[32768]{};
        const DWORD length = GetModuleFileNameW(nullptr, executable, DWORD(std::size(executable)));
        if (length == 0 || length >= std::size(executable))
            throw std::runtime_error("Could not locate the bundled MeanVC2 model.");
        const auto directory = std::filesystem::path(executable).parent_path();
        const auto reference = read_reference(argv[2]);
        const auto model = full_precision
            ? directory / "FullPrecision" / "meanvc2-120ms-40ms-fp32.gguf"
            : directory / "meanvc2-120ms-40ms-q4_k.gguf";
        std::array<float, block_size> input{};
        std::vector<float> output;
        const auto prepare = [&](const char* backend) {
            auto engine = std::make_unique<Converter>(model, reference, backend);
            for (int block = 0; block < 4; ++block)
                engine->run(input, output);
            engine->reset();
            return engine;
        };
        std::unique_ptr<Converter> engine;
        try {
            engine = prepare("vulkan");
        } catch (const std::exception& error) {
            std::fprintf(stderr, "MeanVC2 Vulkan setup: %s. Trying CPU.\n", error.what());
            engine = prepare("cpu");
        }
        if (std::fputs("READY\n", stdout) < 0 || std::fflush(stdout) != 0)
            return 0;
        while (std::fread(input.data(), sizeof(float), input.size(), stdin) == input.size()) {
            engine->run(input, output);
            const int32_t count = static_cast<int32_t>(output.size());
            if (std::fwrite(&count, sizeof(count), 1, stdout) != 1 ||
                std::fwrite(output.data(), sizeof(float), output.size(), stdout) != output.size() ||
                std::fflush(stdout) != 0)
                return 0;
        }
        return std::ferror(stdin) ? 1 : 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "MeanVC2: %s\n", error.what());
        return 1;
    }
}
