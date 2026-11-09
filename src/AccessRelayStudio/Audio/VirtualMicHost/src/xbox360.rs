mod attachment;

use attachment::{usbip_path, Attachment, BUS_ID};
use std::collections::VecDeque;
use std::io::{self, Read, Write};
use std::sync::{Arc, Condvar, Mutex};
use std::thread;
use std::time::{Duration, Instant};
use usbip_audio::{
    IsoEndpoint, IsoTiming, MonotonicClock, Notifier, Server, Setup, Speed, UsbDevice, EPIPE,
};

const STATE_BYTES: usize = 12;
const MAX_PENDING_REPORTS: usize = 64;
const MAX_REPORT_AGE: Duration = Duration::from_millis(250);
const GAMEPAD_IN: u8 = 0x81;
const STATUS_IN: u8 = 0x83;
const OS_VENDOR_REQUEST: u8 = 0x90;
const DEVICE: [u8; 18] = [
    0x12, 0x01, 0x00, 0x02, 0xff, 0xff, 0xff, 0x08, 0x5e, 0x04, 0x8e, 0x02, 0x14, 0x01, 1, 2, 3, 1,
];

// Wired XUSB configuration from ViGEmBus's BSD-3-Clause XusbPdo.cpp.
#[rustfmt::skip]
const CONFIGURATION: [u8; 153] = [
    0x09, 0x02, 0x99, 0x00, 0x04, 0x01, 0x00, 0xa0, 0xfa,
    // Gamepad interface and interrupt endpoints.
    0x09, 0x04, 0x00, 0x00, 0x02, 0xff, 0x5d, 0x01, 0x00,
    0x11, 0x21, 0x00, 0x01, 0x01, 0x25, 0x81, 0x14, 0x00, 0x00, 0x00, 0x00, 0x13, 0x01, 0x08, 0x00, 0x00,
    0x07, 0x05, 0x81, 0x03, 0x20, 0x00, 0x04,
    0x07, 0x05, 0x01, 0x03, 0x20, 0x00, 0x08,
    // Original voice interface layout, with no headset connected.
    0x09, 0x04, 0x01, 0x00, 0x04, 0xff, 0x5d, 0x03, 0x00,
    0x1b, 0x21, 0x00, 0x01, 0x01, 0x01, 0x82, 0x40, 0x01, 0x02, 0x20, 0x16, 0x83, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x16, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x07, 0x05, 0x82, 0x03, 0x20, 0x00, 0x02,
    0x07, 0x05, 0x02, 0x03, 0x20, 0x00, 0x04,
    0x07, 0x05, 0x83, 0x03, 0x20, 0x00, 0x40,
    0x07, 0x05, 0x03, 0x03, 0x20, 0x00, 0x10,
    // Original accessory/security interface descriptors. No authentication is emulated.
    0x09, 0x04, 0x02, 0x00, 0x01, 0xff, 0x5d, 0x02, 0x00,
    0x09, 0x21, 0x00, 0x01, 0x01, 0x22, 0x84, 0x07, 0x00,
    0x07, 0x05, 0x84, 0x03, 0x20, 0x00, 0x10,
    0x09, 0x04, 0x03, 0x00, 0x00, 0xff, 0xfd, 0x13, 0x04,
    0x06, 0x41, 0x00, 0x01, 0x01, 0x03,
];

const BOOT_REPORTS: [&[u8]; 6] = [
    &[0x01, 0x03, 0x0e],
    &[0x02, 0x03, 0x00],
    &[0x03, 0x03, 0x03],
    &[0x08, 0x03, 0x00],
    &[
        0x00, 0x14, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ],
    &[0x01, 0x03, 0x03],
];

#[derive(Default)]
struct State {
    configuration: u8,
    boot_stage: usize,
    input: [u8; STATE_BYTES],
    pending: VecDeque<PendingReport>,
    status_sent: bool,
    polled: bool,
    closed: bool,
}

struct PendingReport {
    input: [u8; STATE_BYTES],
    received: Instant,
}

impl State {
    fn oldest_report_age(&self) -> Duration {
        self.pending
            .front()
            .map_or(Duration::ZERO, |report| report.received.elapsed())
    }

    fn stalled_message(&self) -> String {
        format!("Windows did not consume Xbox input on time. The oldest of {} queued reports waited {} ms.",
            self.pending.len(), self.oldest_report_age().as_millis())
    }
}

#[derive(Default)]
struct Xbox360 {
    state: Mutex<State>,
    ready: Condvar,
    interrupt: Notifier,
}

impl Xbox360 {
    fn update(&self, input: [u8; STATE_BYTES]) -> io::Result<()> {
        let mut state = self.state.lock().unwrap();
        if state.input == input {
            return Ok(());
        }
        if state.pending.len() == MAX_PENDING_REPORTS || state.oldest_report_age() >= MAX_REPORT_AGE
        {
            return Err(io::Error::other(state.stalled_message()));
        }
        state.input = input;
        state.pending.push_back(PendingReport {
            input,
            received: Instant::now(),
        });
        drop(state);
        self.interrupt.notify();
        Ok(())
    }

    fn close_input(&self) {
        let mut state = self.state.lock().unwrap();
        state.closed = true;
        let neutral = [0; STATE_BYTES];
        if state.input != neutral || !state.pending.is_empty() {
            state.pending.clear();
            state.input = neutral;
            state.pending.push_back(PendingReport {
                input: neutral,
                received: Instant::now(),
            });
        }
        drop(state);
        self.ready.notify_all();
        self.interrupt.notify();
    }

    fn wait_for_first_poll(&self, timeout: Duration) -> Result<bool, String> {
        let (state, _) = self
            .ready
            .wait_timeout_while(self.state.lock().unwrap(), timeout, |state| {
                !state.polled && !state.closed
            })
            .unwrap();
        if state.closed {
            return Ok(false);
        }
        if !state.polled {
            return Err("Windows did not start polling the virtual Xbox controller.".into());
        }
        Ok(true)
    }

    fn wait_for_input_end(&self) -> Result<(), String> {
        let mut state = self.state.lock().unwrap();
        while !state.closed {
            if state.oldest_report_age() >= MAX_REPORT_AGE {
                return Err(state.stalled_message());
            }
            state = self
                .ready
                .wait_timeout(state, Duration::from_millis(25))
                .unwrap()
                .0;
        }
        Ok(())
    }
}

fn input_report(state: &[u8; STATE_BYTES]) -> Vec<u8> {
    let mut report = vec![0; 20];
    report[1] = 0x14;
    report[2..2 + STATE_BYTES].copy_from_slice(state);
    report
}

fn string_descriptor(text: &str) -> Vec<u8> {
    let mut data = vec![2, 3];
    data.extend(text.encode_utf16().flat_map(u16::to_le_bytes));
    data[0] = data.len() as u8;
    data
}

fn descriptor(value: u16) -> Result<Vec<u8>, i32> {
    Ok(match value {
        0x0100 => DEVICE.to_vec(),
        0x0200 => CONFIGURATION.to_vec(),
        0x0300 => vec![4, 3, 0x09, 0x04],
        0x0301 => string_descriptor("AccessRelay"),
        0x0302 => string_descriptor("AccessRelay Virtual Xbox 360 Controller"),
        0x0303 => string_descriptor("AccessRelay-VirtualXbox-v1"),
        0x0304 => string_descriptor("XUSB accessory interface"),
        0x03ee => {
            let mut data = string_descriptor("MSFT100");
            data.extend([OS_VENDOR_REQUEST, 0]);
            data[0] = data.len() as u8;
            data
        }
        _ => return Err(EPIPE),
    })
}

fn compatible_id_descriptor() -> Vec<u8> {
    let mut data = vec![0; 40];
    data[0] = 40;
    data[5] = 1;
    data[6] = 4;
    data[8] = 1;
    data[17] = CONFIGURATION[4];
    data[18..24].copy_from_slice(b"XUSB10");
    data
}

impl UsbDevice for Xbox360 {
    fn speed(&self) -> Speed {
        Speed::Full
    }

    fn device_descriptor(&self) -> Vec<u8> {
        DEVICE.to_vec()
    }

    fn configuration_descriptor(&self) -> Vec<u8> {
        CONFIGURATION.to_vec()
    }

    fn control(&self, setup: Setup, _data: &[u8]) -> Result<Vec<u8>, i32> {
        let mut data = match (setup.request_type, setup.request, setup.value, setup.index) {
            (0x80, 0x06, value, _) => descriptor(value)?,
            (0x80 | 0x82, 0, 0, _) => vec![0, 0],
            (0x00, 0x05, 0..=127, 0) | (0x00, 0x01 | 0x03, 1, 0) | (0x02, 0x01, 0, _) => vec![],
            (0x80, 0x08, 0, 0) => vec![self.state.lock().unwrap().configuration],
            (0x00, 0x09, configuration @ 0..=1, 0) => {
                let mut state = self.state.lock().unwrap();
                state.configuration = configuration as u8;
                if configuration == 0 {
                    state.boot_stage = 0;
                    state.status_sent = false;
                }
                vec![]
            }
            (0xc0, OS_VENDOR_REQUEST, 0, 4) => compatible_id_descriptor(),
            (0xc0, 0x01, 0, 0) if setup.length == 4 => vec![0x31, 0x3f, 0xcf, 0xdc],
            (0xa1, 0x01, 0x0100, 0) => input_report(&self.state.lock().unwrap().input),
            (0x21, 0x09, 0x0200, 0) => vec![],
            _ => return Err(EPIPE),
        };
        data.truncate(setup.length as usize);
        Ok(data)
    }

    fn iso_endpoint(&self, _address: u8) -> Option<IsoEndpoint> {
        None
    }
    fn iso_out(&self, _address: u8, _timing: IsoTiming, _packets: &[&[u8]]) {}
    fn iso_in(
        &self,
        _address: u8,
        _timing: IsoTiming,
        _capacity: &[usize],
        _data: &mut Vec<u8>,
    ) -> Vec<usize> {
        vec![]
    }

    fn interrupt_endpoint(&self, address: u8) -> Option<Notifier> {
        matches!(address, GAMEPAD_IN | 0x82 | STATUS_IN | 0x84).then(|| self.interrupt.clone())
    }

    fn interrupt_in(&self, address: u8, capacity: usize) -> Option<Vec<u8>> {
        let mut state = self.state.lock().unwrap();
        let mut report = match address {
            GAMEPAD_IN if capacity >= 3 => {
                if !state.polled {
                    state.polled = true;
                    self.ready.notify_all();
                }
                if let Some(report) = BOOT_REPORTS.get(state.boot_stage) {
                    state.boot_stage += 1;
                    report.to_vec()
                } else if state.oldest_report_age() >= MAX_REPORT_AGE {
                    return None;
                } else {
                    input_report(&state.pending.pop_front()?.input)
                }
            }
            STATUS_IN if capacity >= 3 && !state.status_sent => {
                state.status_sent = true;
                vec![0x05, 0x03, 0x00]
            }
            _ => return None,
        };
        report.truncate(capacity);
        Some(report)
    }
}

fn read_input(mut input: impl Read, device: &Xbox360) -> io::Result<()> {
    let mut packet = [0; STATE_BYTES];
    while input.read(&mut packet[..1])? != 0 {
        input.read_exact(&mut packet[1..])?;
        device.update(packet)?;
    }
    Ok(())
}

fn run() -> Result<(), String> {
    let executable = usbip_path()?;
    let device = Arc::new(Xbox360::default());
    let server =
        Server::start(0, Arc::new(MonotonicClock::new())).map_err(|error| error.to_string())?;
    server.add(BUS_ID, device.clone());
    let input_device = device.clone();
    let reader = thread::spawn(move || {
        let result = read_input(io::stdin().lock(), &input_device);
        input_device.close_input();
        result
    });
    let _attachment = Attachment::new(executable, server.port(), "AccessRelay Xbox controller")?;
    if device.wait_for_first_poll(Duration::from_secs(10))? {
        let mut stdout = io::stdout().lock();
        writeln!(stdout, "READY")
            .and_then(|_| stdout.flush())
            .map_err(|error| error.to_string())?;
    }
    if let Err(error) = device.wait_for_input_end() {
        device.close_input();
        return Err(error);
    }
    reader
        .join()
        .map_err(|_| "The Xbox report reader failed.")?
        .map_err(|error| format!("AccessRelay Xbox controller input closed: {error}"))
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

    #[test]
    fn descriptors_have_expected_id_and_lengths() {
        let mut offset = 0;
        let mut interfaces = 0;
        while offset < CONFIGURATION.len() {
            assert!(CONFIGURATION[offset] >= 2);
            interfaces += (CONFIGURATION[offset + 1] == 4) as u8;
            offset += CONFIGURATION[offset] as usize;
        }
        assert_eq!(offset, CONFIGURATION.len());
        assert_eq!(interfaces, CONFIGURATION[4]);
        assert_eq!(&DEVICE[8..12], &[0x5e, 0x04, 0x8e, 0x02]);
        assert_eq!(descriptor(0x03ee).unwrap()[16], OS_VENDOR_REQUEST);
        assert_eq!(&compatible_id_descriptor()[18..26], b"XUSB10\0\0");
    }

    #[test]
    fn readiness_requires_gamepad_poll_and_reports_preserve_xinput_bytes() {
        let device = Xbox360::default();
        assert!(device.wait_for_first_poll(Duration::ZERO).is_err());
        assert_eq!(device.interrupt_in(STATUS_IN, 32), Some(vec![5, 3, 0]));
        assert!(device.wait_for_first_poll(Duration::ZERO).is_err());
        for boot in BOOT_REPORTS {
            assert_eq!(device.interrupt_in(GAMEPAD_IN, 32), Some(boot.to_vec()));
        }
        assert_eq!(device.wait_for_first_poll(Duration::ZERO), Ok(true));
        let packet = [
            0x01, 0x10, 0xff, 0x00, 0x00, 0x80, 0xff, 0x7f, 0, 0, 0x01, 0x80,
        ];
        read_input(packet.as_slice(), &device).unwrap();
        let report = device.interrupt_in(GAMEPAD_IN, 32).unwrap();
        assert_eq!(&report[..2], &[0, 0x14]);
        assert_eq!(&report[2..14], &packet);
        assert_eq!(&report[14..], &[0; 6]);
        assert_eq!(device.interrupt_in(GAMEPAD_IN, 32), None);
        assert!(read_input(&packet[..STATE_BYTES - 1], &device).is_err());
        device.close_input();
        assert_eq!(device.wait_for_first_poll(Duration::ZERO), Ok(false));
    }

    #[test]
    fn queued_transitions_keep_short_taps_and_stop_on_overload() {
        let device = Xbox360::default();
        for _ in BOOT_REPORTS {
            device.interrupt_in(GAMEPAD_IN, 32).unwrap();
        }
        let mut pressed = [0; STATE_BYTES];
        pressed[1] = 0x10; // A
        let neutral = [0; STATE_BYTES];
        device.update(pressed).unwrap();
        device.update(pressed).unwrap();
        device.update(neutral).unwrap();
        assert_eq!(
            device.interrupt_in(GAMEPAD_IN, 32),
            Some(input_report(&pressed))
        );
        assert_eq!(
            device.interrupt_in(GAMEPAD_IN, 32),
            Some(input_report(&neutral))
        );
        assert_eq!(device.interrupt_in(GAMEPAD_IN, 32), None);

        for index in 1..=MAX_PENDING_REPORTS {
            pressed[2] = index as u8;
            device.update(pressed).unwrap();
        }
        assert!(device.update(neutral).is_err());
        device.close_input();
        assert_eq!(
            device.interrupt_in(GAMEPAD_IN, 32),
            Some(input_report(&neutral))
        );
        assert_eq!(device.interrupt_in(GAMEPAD_IN, 32), None);
    }

    #[test]
    fn longer_poll_gap_retains_order_and_stale_reports_cannot_replay() {
        let device = Xbox360::default();
        for _ in BOOT_REPORTS {
            device.interrupt_in(GAMEPAD_IN, 32).unwrap();
        }
        // Model 128 ms without USB polls at a 4 ms producer cadence.
        for index in 1..=32 {
            let mut input = [0; STATE_BYTES];
            input[1] = if index % 2 == 1 { 0x10 } else { 0 };
            input[4] = index;
            device.update(input).unwrap();
        }
        for index in 1..=32 {
            let report = device.interrupt_in(GAMEPAD_IN, 32).unwrap();
            assert_eq!(report[3], if index % 2 == 1 { 0x10 } else { 0 });
            assert_eq!(report[6], index);
        }

        device.update([1; STATE_BYTES]).unwrap();
        device
            .state
            .lock()
            .unwrap()
            .pending
            .front_mut()
            .unwrap()
            .received = Instant::now() - MAX_REPORT_AGE;
        assert!(device.interrupt_in(GAMEPAD_IN, 32).is_none());
        assert!(device
            .wait_for_input_end()
            .unwrap_err()
            .contains("queued reports"));
    }
}
