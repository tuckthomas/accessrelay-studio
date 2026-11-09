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
constexpr int model_rate = 44100;
constexpr size_t block_size = 7680;
constexpr size_t model_block = block_size * model_rate / app_rate;
constexpr size_t model_hop = model_rate / 25;
constexpr size_t model_window = ((model_block * 2 + model_hop - 1) / model_hop) * model_hop;

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

    const size_t frames = std::min<size_t>(data_length / 2, rate * 3);
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
    Converter(const std::filesystem::path& model_path, const std::vector<float>& reference)
        : request_(audiocpp_request_create(), audiocpp_request_free) {
        if (!request_)
            throw std::runtime_error("Could not allocate the MioCodec model request.");
        audiocpp_registry* registry = nullptr;
        check(audiocpp_registry_create(nullptr, &registry));
        registry_.reset(registry);

        const auto model_file = model_path.u8string();
        audiocpp_model* model = nullptr;
        const audiocpp_model_config selection{"miocodec", nullptr, nullptr, "embedded"};
        check(audiocpp_model_load(registry_.get(), reinterpret_cast<const char*>(model_file.c_str()),
                                  &selection, nullptr, &model));
        model_.reset(model);

        audiocpp_session* session = nullptr;
        const audiocpp_backend_config vulkan{"vulkan", 0, 6};
        Handle<audiocpp_options, audiocpp_options_free> options(audiocpp_options_create(), audiocpp_options_free);
        if (!options)
            throw std::runtime_error("Could not allocate the MioCodec model options.");
        check(audiocpp_options_set(options.get(), "miocodec.weight_type", "native"));
        check(audiocpp_session_create(model_.get(), "vc", "offline", &vulkan, options.get(), &session));
        session_.reset(session);
        check(audiocpp_request_set_voice_audio(request_.get(), reference.data(), reference.size(), model_rate, 1));
    }

    void run(std::array<float, block_size>& input, std::array<float, block_size>& output) {
        for (auto& sample : input)
            sample = finite_sample(sample);
        std::copy(previous_.begin(), previous_.end(), context_.begin());
        std::copy(input.begin(), input.end(), context_.begin() + block_size);
        previous_ = input;
        resample(context_.data(), context_.size(), app_rate, source_.data(), model_block * 2, model_rate);
        std::fill(source_.begin() + model_block * 2, source_.end(), 0.0f);
        check(audiocpp_request_set_audio(request_.get(), source_.data(), source_.size(), model_rate, 1));

        audiocpp_result* result = nullptr;
        check(audiocpp_session_run(session_.get(), request_.get(), &result));
        Handle<audiocpp_result, audiocpp_result_free> converted(result, audiocpp_result_free);
        const float* samples = nullptr;
        size_t frames = 0;
        int rate = 0, channels = 0;
        check(audiocpp_result_audio(converted.get(), &samples, &frames, &rate, &channels));
        if (!samples || frames < model_block * 2 || rate != model_rate || channels != 1)
            throw std::runtime_error("MioCodec returned an unexpected audio format.");
        resample(samples + model_block, frames - model_block, model_rate, output.data(), output.size(), app_rate);
        for (auto& sample : output)
            sample = finite_sample(sample);

        // Blend the block's first few samples toward the prior endpoint to suppress seams.
        constexpr size_t ramp = app_rate / 1000;
        const float offset = last_output_ - output.front();
        for (size_t i = 0; i < ramp; ++i)
            output[i] = finite_sample(output[i] + offset * (1.0f - static_cast<float>(i) / ramp));
        last_output_ = output.back();
    }

    void reset() {
        previous_.fill(0);
        last_output_ = 0;
    }

private:
    Handle<audiocpp_registry, audiocpp_registry_free> registry_{nullptr, audiocpp_registry_free};
    Handle<audiocpp_model, audiocpp_model_free> model_{nullptr, audiocpp_model_free};
    Handle<audiocpp_session, audiocpp_session_free> session_{nullptr, audiocpp_session_free};
    Handle<audiocpp_request, audiocpp_request_free> request_;
    std::array<float, block_size> previous_{};
    std::array<float, block_size * 2> context_{};
    std::array<float, model_window> source_{};
    float last_output_ = 0;
};
}

int wmain(int argc, wchar_t** argv) {
    try {
        if (argc != 3 || std::wstring_view(argv[1]) != L"--reference")
            throw std::runtime_error("Expected --reference followed by the local voice profile WAV.");
        _setmode(_fileno(stdin), _O_BINARY);
        _setmode(_fileno(stdout), _O_BINARY);

        wchar_t executable[32768]{};
        const DWORD length = GetModuleFileNameW(nullptr, executable, DWORD(std::size(executable)));
        if (length == 0 || length >= std::size(executable))
            throw std::runtime_error("Could not locate the bundled MioCodec model.");
        const auto directory = std::filesystem::path(executable).parent_path();
        const auto reference = read_reference(argv[2]);
        Converter engine(directory / "miocodec-25hz-44khz-v2-q8_0.gguf", reference);
        std::array<float, block_size> input{}, output{};
        engine.run(input, output);
        engine.reset();
        if (std::fputs("READY\n", stdout) < 0 || std::fflush(stdout) != 0)
            return 0;
        while (std::fread(input.data(), sizeof(float), input.size(), stdin) == input.size()) {
            engine.run(input, output);
            constexpr int32_t count = block_size;
            if (std::fwrite(&count, sizeof(count), 1, stdout) != 1 ||
                std::fwrite(output.data(), sizeof(float), output.size(), stdout) != output.size() ||
                std::fflush(stdout) != 0)
                return 0;
        }
        return std::ferror(stdin) ? 1 : 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "MioCodec: %s\n", error.what());
        return 1;
    }
}
