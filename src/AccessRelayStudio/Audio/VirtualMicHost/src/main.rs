mod attachment;

use attachment::{usbip_path, Attachment, BUS_ID};
use std::collections::VecDeque;
use std::io::{self, Read, Write};
use std::sync::{Arc, Mutex};
use usbip_audio::uac2::{Uac2Config, Uac2Device, Uac2Streams};
use usbip_audio::{MonotonicClock, Server};

const SAMPLE_RATE: u32 = 48_000;
const PCM_BLOCK_BYTES: usize = (SAMPLE_RATE as usize / 200) * 4;
const DEFAULT_PRIME_MS: usize = 60;
const BUFFER_HEADROOM_MS: usize = 320;
const MAX_BUFFER_MS: usize = 900;
const FADE_SAMPLES: usize = SAMPLE_RATE as usize * 2 / 1000;
const STARTUP_RESERVE_SAMPLES: usize = SAMPLE_RATE as usize * 20 / 1000;
const CAPTURE_IDLE_FRAMES: u64 = SAMPLE_RATE as u64 * BUFFER_HEADROOM_MS as u64 / 1000;
const MIC_IDENTITY_FILE: &str = "AccessRelay.VirtualMicUsbId.txt";

fn parse_microphone_usb_id(text: &str) -> Option<(u16, u16)> {
    let text = text.trim();
    // pid.codes reserves 1209:0001 for private in-house testing. The marked build must not be distributed.
    if text == "1209:0001 PRIVATE-TEST" {
        return Some((0x1209, 0x0001));
    }
    let (vid, pid) = text.split_once(':')?;
    let parse = |hex: &str| {
        if hex.len() != 4 || !hex.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return None;
        }
        u16::from_str_radix(hex, 16).ok()
    };
    let (vid, pid) = (parse(vid)?, parse(pid)?);
    if matches!(vid, 0 | u16::MAX) || matches!(pid, 0 | u16::MAX) || (vid == 0x1209 && pid < 0x2000)
    {
        return None;
    }
    Some((vid, pid))
}

fn microphone_usb_id() -> Result<(u16, u16), String> {
    let helper = std::env::current_exe().map_err(|error| error.to_string())?;
    let identity = std::fs::read_to_string(helper.with_file_name(MIC_IDENTITY_FILE))
        .ok()
        .and_then(|text| parse_microphone_usb_id(&text));
    identity.ok_or_else(|| "Virtual microphone output requires a configured USB identity. See the virtual device build instructions.".into())
}

#[derive(Clone, Copy, Default)]
struct BufferStats {
    underruns: usize,
    dropped_samples: usize,
    largest_request: usize,
}

struct Samples {
    queue: VecDeque<i32>,
    capacity: usize,
    prime_samples: usize,
    primed: bool,
    capture_end: Option<u64>,
    discarded_audio: bool,
    last_output: i32,
    fade_from: i32,
    fade_remaining: usize,
    stats: BufferStats,
}

impl Samples {
    fn new(prime_ms: usize) -> Self {
        let prime_ms = prime_ms.clamp(1, MAX_BUFFER_MS - BUFFER_HEADROOM_MS);
        let capacity = SAMPLE_RATE as usize * (prime_ms + BUFFER_HEADROOM_MS) / 1000;
        Self {
            queue: VecDeque::with_capacity(capacity),
            capacity,
            prime_samples: SAMPLE_RATE as usize * prime_ms / 1000,
            primed: false,
            capture_end: None,
            discarded_audio: false,
            last_output: 0,
            fade_from: 0,
            fade_remaining: 0,
            stats: BufferStats::default(),
        }
    }

    fn write(&mut self, bytes: &[u8]) {
        let excess = (self.queue.len() + bytes.len() / 4).saturating_sub(self.capacity);
        let drop_queued = excess.min(self.queue.len());
        if excess > 0 {
            self.queue.drain(..drop_queued);
            self.stats.dropped_samples += excess;
            self.discarded_audio = true;
        }

        for word in bytes.chunks_exact(4).skip(excess - drop_queued) {
            let sample = f32::from_le_bytes(word.try_into().unwrap());
            let sample = if sample.is_finite() {
                sample.clamp(-1.0, 1.0)
            } else {
                0.0
            };
            self.queue
                .push_back((sample * i32::MAX as f32) as i32 & !0xff);
        }
    }

    fn capture(&mut self, frame: u64, output: &mut [i32]) {
        if output.is_empty() {
            return;
        }
        self.stats.largest_request = self.stats.largest_request.max(output.len());

        let required = self.prime_samples.max(output.len().min(self.capacity));
        if self
            .capture_end
            .is_none_or(|end| frame.saturating_sub(end) >= CAPTURE_IDLE_FRAMES)
        {
            // With no capture client the queue can contain speech from well before the current request.
            let stale = self
                .queue
                .len()
                .saturating_sub(required + STARTUP_RESERVE_SAMPLES);
            self.queue.drain(..stale);
            self.primed = false;
            self.last_output = 0;
            self.fade_remaining = 0;
        }
        self.capture_end = Some(frame.saturating_add(output.len() as u64));
        if !self.primed && self.queue.len() < required {
            self.silence(output);
            return;
        }
        if !self.primed || self.discarded_audio {
            self.begin_fade();
        }
        self.primed = true;
        self.discarded_audio = false;

        let count = self.queue.len().min(output.len());
        for sample in &mut output[..count] {
            let next = self.queue.pop_front().unwrap();
            *sample = self.render(next);
        }
        if count < output.len() {
            self.stats.underruns += 1;
            self.primed = false;
            self.begin_fade();
            self.silence(&mut output[count..]);
        }
    }

    fn begin_fade(&mut self) {
        self.fade_from = self.last_output;
        self.fade_remaining = FADE_SAMPLES;
    }

    fn render(&mut self, next: i32) -> i32 {
        let output = if self.fade_remaining == 0 {
            next
        } else {
            self.fade_remaining -= 1;
            let previous_weight = self.fade_remaining as i64;
            let next_weight = (FADE_SAMPLES - self.fade_remaining) as i64;
            let mixed = i64::from(self.fade_from) * previous_weight + i64::from(next) * next_weight;
            (mixed / FADE_SAMPLES as i64) as i32 & !0xff
        };
        self.last_output = output;
        output
    }

    fn silence(&mut self, output: &mut [i32]) {
        let fading = self.fade_remaining.min(output.len());
        for sample in &mut output[..fading] {
            *sample = self.render(0);
        }
        output[fading..].fill(0);
    }
}

struct Microphone(Mutex<Samples>);

impl Uac2Streams for Microphone {
    fn playback(&self, _frame: u64, _samples: &[i32]) {}

    fn capture(&self, frame: u64, samples: &mut [i32]) {
        self.0.lock().unwrap().capture(frame, samples);
    }
}

fn run() -> Result<(), String> {
    let (vendor_id, product_id) = microphone_usb_id()?;
    let executable = usbip_path()?;
    let prime_ms = std::env::var("ACCESSRELAY_MIC_PRIME_MS")
        .ok()
        .and_then(|value| value.parse().ok())
        .unwrap_or(DEFAULT_PRIME_MS);

    let microphone = Arc::new(Microphone(Mutex::new(Samples::new(prime_ms))));
    let config = Uac2Config {
        vendor_id,
        product_id,
        bcd_device: 0x0100,
        manufacturer: "AccessRelay".into(),
        product: "AccessRelay Virtual Microphone".into(),
        serial: "AccessRelay-VirtualMic-v1".into(),
        sample_rate: SAMPLE_RATE,
        playback_channels: 0,
        capture_channels: 1,
        playback_name: None,
        capture_name: Some("AccessRelay Virtual Microphone".into()),
        capture_connector: false,
    };
    let device = Uac2Device::new(config, microphone.clone())?;
    let server =
        Server::start(0, Arc::new(MonotonicClock::new())).map_err(|error| error.to_string())?;
    server.add(BUS_ID, Arc::new(device));
    let _attachment = Attachment::new(executable, server.port(), "AccessRelay microphone")?;
    let mut stdout = io::stdout().lock();
    writeln!(stdout, "READY")
        .and_then(|_| stdout.flush())
        .map_err(|error| error.to_string())?;

    let mut input = io::stdin().lock();
    let mut buffer = [0; PCM_BLOCK_BYTES];
    let result = loop {
        match input.read_exact(&mut buffer) {
            Ok(()) => microphone.0.lock().unwrap().write(&buffer),
            Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => break Ok(()),
            Err(error) => break Err(format!("AccessRelay audio input closed: {error}")),
        }
    };
    let stats = microphone.0.lock().unwrap().stats;
    eprintln!(
        "AccessRelay microphone buffer: underruns={}, dropped_samples={}, largest_request_samples={}",
        stats.underruns, stats.dropped_samples, stats.largest_request
    );
    result
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const LEVEL: i32 = 0x2000_0000;

    #[test]
    fn microphone_usb_identity_requires_an_assigned_or_marked_private_id() {
        assert_eq!(
            parse_microphone_usb_id("1209:20ab\n"),
            Some((0x1209, 0x20ab))
        );
        assert_eq!(
            parse_microphone_usb_id("1209:0001 PRIVATE-TEST"),
            Some((0x1209, 1))
        );
        for invalid in [
            "",
            "1209:0001",
            "1209:1000",
            "1209:+ABC",
            "1209:１２３４",
            "0000:2000",
            "1209:FFFF",
        ] {
            assert_eq!(parse_microphone_usb_id(invalid), None);
        }
    }

    #[test]
    fn clamps_to_signed_24_bit_samples_and_rejects_nonfinite_input() {
        let mut samples = Samples::new(DEFAULT_PRIME_MS);
        for value in [2.0f32, -2.0, 0.25, f32::NAN, f32::INFINITY] {
            samples.write(&value.to_le_bytes());
        }
        assert_eq!(
            samples.queue,
            [i32::MAX & !0xff, i32::MIN, 0x2000_0000, 0, 0]
        );
    }

    #[test]
    fn startup_and_jitter_preserve_the_fifo() {
        let mut samples = Samples::new(3);
        let input: Vec<i32> = (0..256).map(|index| LEVEL + index * 256).collect();
        samples.queue.extend(&input[..120]);
        let mut output = [1; 64];
        samples.capture(0, &mut output);
        assert_eq!(output, [0; 64]);
        assert_eq!(samples.queue.len(), 120);
        assert_eq!(samples.stats.underruns, 0);

        samples.queue.extend(&input[120..160]);
        samples.capture(64, &mut output);
        assert!(output[0] > 0 && output[0] < LEVEL);
        samples.capture(128, &mut output);
        assert_eq!(&output[32..], &input[96..128]);
        samples.queue.extend(&input[160..]);
        samples.capture(192, &mut output);
        assert_eq!(output, input[128..192]);
        assert_eq!(samples.stats.underruns, 0);
    }

    #[test]
    fn fades_through_a_real_underrun_and_recovers_after_repriming() {
        let mut samples = Samples::new(2);
        samples
            .queue
            .extend(std::iter::repeat_n(LEVEL, FADE_SAMPLES + 1));
        let mut output = [0; FADE_SAMPLES];
        samples.capture(0, &mut output);
        assert_eq!(output[FADE_SAMPLES - 1], LEVEL);

        let mut short = [0; FADE_SAMPLES / 2];
        samples.capture(FADE_SAMPLES as u64, &mut short);
        assert_eq!(short[0], LEVEL);
        assert!(short.windows(2).all(|pair| pair[0] > pair[1]));
        assert_eq!(samples.stats.underruns, 1);
        samples.capture((FADE_SAMPLES + short.len()) as u64, &mut output);
        assert!(output[0] > 0 && output[0] < short[short.len() - 1]);
        assert!(output[short.len()..].iter().all(|sample| *sample == 0));
        assert_eq!(samples.stats.underruns, 1);

        samples
            .queue
            .extend(std::iter::repeat_n(-LEVEL, FADE_SAMPLES));
        samples.capture((FADE_SAMPLES * 2 + short.len()) as u64, &mut output);
        assert!(output[0] < 0 && output[0] > -LEVEL);
        assert_eq!(output[FADE_SAMPLES - 1], -LEVEL);
        assert_eq!(samples.stats.underruns, 1);
    }

    #[test]
    fn overflow_keeps_recent_audio_and_fades_over_the_cut() {
        let mut samples = Samples::new(2);
        let mut output = [0; FADE_SAMPLES];
        samples
            .queue
            .extend(std::iter::repeat_n(LEVEL, FADE_SAMPLES + 8));
        samples.capture(0, &mut output);
        let replacement = (-0.25f32).to_le_bytes().repeat(samples.capacity + 4);
        samples.write(&replacement);
        assert_eq!(samples.queue.len(), samples.capacity);
        assert_eq!(samples.stats.dropped_samples, 12);
        assert_eq!(samples.queue.front(), Some(&-LEVEL));
        samples.capture(FADE_SAMPLES as u64, &mut output);
        assert!(output[0] > 0 && output[0] < LEVEL);
        assert_eq!(output[FADE_SAMPLES - 1], -LEVEL);
    }

    #[test]
    fn an_oversized_usb_request_plays_buffered_audio_then_silence() {
        let mut samples = Samples::new(usize::MAX);
        assert_eq!(
            samples.capacity,
            SAMPLE_RATE as usize * MAX_BUFFER_MS / 1000
        );
        samples
            .queue
            .extend(std::iter::repeat_n(LEVEL, samples.capacity));
        let mut output = vec![1; samples.capacity + FADE_SAMPLES];
        samples.capture(0, &mut output);
        assert_eq!(output[samples.capacity - 1], LEVEL);
        assert!(output[samples.capacity] > 0 && output[samples.capacity] < LEVEL);
        assert_eq!(output.last(), Some(&0));
        assert_eq!(samples.stats.underruns, 1);
        assert_eq!(samples.stats.largest_request, output.len());
    }

    #[test]
    fn first_capture_and_resubscribe_trim_idle_audio() {
        let mut samples = Samples::new(2);
        let mut output = [0; FADE_SAMPLES];
        let retained = samples.prime_samples + STARTUP_RESERVE_SAMPLES;
        for (index, level) in [LEVEL, -LEVEL].into_iter().enumerate() {
            samples.queue.clear();
            samples.queue.extend(std::iter::repeat_n(-level, retained));
            samples.queue.extend(std::iter::repeat_n(level, retained));
            let frame = index as u64 * (CAPTURE_IDLE_FRAMES + output.len() as u64);
            samples.capture(frame, &mut output);
            assert_eq!(samples.queue.len(), retained - output.len());
            assert_eq!(output[FADE_SAMPLES - 1], level);
            assert!(output[0].abs() < LEVEL);
        }
        assert_eq!(samples.stats.underruns, 0);
    }
}
