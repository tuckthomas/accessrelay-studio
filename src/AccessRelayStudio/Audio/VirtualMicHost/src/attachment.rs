use std::path::{Path, PathBuf};
use std::process::Command;

pub const BUS_ID: &str = "1-1";

pub fn usbip_path() -> Result<PathBuf, String> {
    let mut args = std::env::args_os().skip(1);
    args.next()
        .map(PathBuf::from)
        .filter(|path| path.is_absolute() && path.is_file() && args.next().is_none())
        .ok_or_else(|| "Expected the absolute path to usbip.exe.".into())
}

fn usbip_command(executable: &Path) -> Command {
    let mut command = Command::new(executable);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    command
}

pub struct Attachment {
    executable: PathBuf,
    port: u32,
}

impl Attachment {
    pub fn new(executable: PathBuf, server_port: u16, device: &str) -> Result<Self, String> {
        let output = usbip_command(&executable)
            .args([
                "--tcp-port",
                &server_port.to_string(),
                "attach",
                "--remote",
                "127.0.0.1",
                "--bus-id",
                BUS_ID,
                "--once",
                "--terse",
            ])
            .output()
            .map_err(|error| format!("Could not run the bundled USB/IP client: {error}"))?;
        let text = String::from_utf8_lossy(&output.stdout);
        if !output.status.success() {
            let diagnostic = String::from_utf8_lossy(&output.stderr);
            return Err(format!("Could not attach the {device}: {text}{diagnostic}"));
        }
        let port = text
            .trim()
            .parse()
            .map_err(|_| format!("USB/IP returned no attached port: {text:?}"))?;
        Ok(Self { executable, port })
    }
}

impl Drop for Attachment {
    fn drop(&mut self) {
        let _ = usbip_command(&self.executable)
            .args(["detach", "--port", &self.port.to_string()])
            .output();
    }
}
