# Connection setup

Headset link status, receiver diagnostics and USB recovery all talk to the Frame itself.
Set them up on the **Connection** page.

The **Dongle USB** readout on Overview, Diagnostics and Connection checks the actual
PC-to-dongle connection every five seconds. **USB 2** highlights a bandwidth bottleneck;
connect the dongle directly to a USB 3 port and check any hub or extension. **USB 3**
confirms SuperSpeed operation but does not guarantee a particular wireless video bitrate.
The readout needs neither headset pairing nor administrator access. It queries
[Windows USB hub operating-speed flags](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usbioctl/ns-usbioctl-_usb_node_connection_information_ex_v2_flags),
not the network adapter's configured USB mode or Wi-Fi rate. Failed reads show **unknown**,
and stale results expire after 15 seconds.

## Wireless monitoring (recommended)

1. Enable **Developer Mode** on the Frame and pair this PC using **SteamOS Devkit**.
2. Keep the Frame awake with Wi-Fi enabled.
3. Enter:
   - the Frame's wireless hostname or IP address;
   - the paired Devkit private key, by default `%LOCALAPPDATA%\steamos-devkit\devkit_rsa`;
   - an existing, trusted OpenSSH known-hosts file containing the Frame's host key.
4. Click **Save and test connection**.

Requirements: the Windows OpenSSH client (`C:\Windows\System32\OpenSSH\ssh.exe`) and Python 3
on the headset, which SteamOS provides.

How it connects:

- Always Windows' own OpenSSH client, never whatever `ssh` is on PATH.
- Only the configured key and known-hosts file. Your SSH config and global host files are ignored.
- Strict host checking. An unknown device is never auto-trusted; pair it outside the app first.
- Read-only queries as the `steamos` user. No root, no installed helpers, no forwarded ports.

## Optional: ADB link status

If no wireless host is set, the app can read link status over ADB instead.

1. Enable Developer Mode on the Frame and authorize this PC for ADB.
2. Set the path to `adb.exe`. It's found automatically in the Android SDK or on PATH;
   ADB is not bundled.
3. If several devices are connected, enter the Frame's ADB serial.

The app only creates and removes its own temporary port forward and never touches the ADB server.
A configured wireless host always uses SSH and never falls back to USB.

## Recovering a stalled USB connection

USB can join a stream that was discovered over Wi-Fi; Wi-Fi must stay enabled.
If the USB link stalls, **Restart Frame USB connection** asks the headset to restart its USB daemon.

- It uses the same host, key and known-hosts file as wireless monitoring.
- It requires a previously installed USB daemon fix on the headset and verifies those
  root-owned files and the supervised daemon process before signalling it.
- It does not request root, install firmware, change Wi-Fi, or accept unknown host keys.

Recovery restarts a stalled connection. The underlying USB receive stall is not fixed by it.
Reconnect the stream once the USB adapter comes back.
