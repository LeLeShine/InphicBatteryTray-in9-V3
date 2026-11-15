# InphicBatteryTray (INPHIC IN9 V3 battery tray)

[中文](README.md) | [**English**](README.en.md)

A **taskbar battery indicator** for the INPHIC IN9 V3 wireless mouse. No vendor app required — as soon as the mouse connects, the tray icon fills according to the real charge level.

> **Tested scope**
> This project has been verified on real hardware **only with the INPHIC IN9 V3** 2.4G receiver:
> `VID_249A & PID_5C2F`, interface `MI_02`, 33-byte input and output reports, no Feature report.
> The IN10 (`1D57` / `045E`) and legacy IN9 (`248A`, Feature Report `0x0C`) code paths come from the upstream projects and were **not** verified in this repository. They are kept for compatibility only.

In the notification area:

![In the tray](assets/a1.png)

At different charge levels:

![Battery levels](assets/preview-levels.png)

---

## Why this exists

The vendor config tool shows battery only inside its own UI, and only after you open the app and navigate to the device page. Close it and you have no idea how much charge is left.

Windows' built-in "Bluetooth device battery" does not work for 2.4G receivers, so this had to be built by hand.

---

## Features

| Feature | Detail |
|---------|--------|
| Battery icon | Mouse silhouette filled from the bottom up; teal (>40%) / yellow (16–40%) / red (≤15%) |
| Charging state | Details show "not charging" or "charging" |
| Charging fix | Firmware reports a fixed 100% while charging; the app keeps showing the last real level instead |
| Offline detection | No data for 8 s → icon greys out; tooltip still shows the last known level |
| Detail popup | Non-modal, never steals focus, auto-dismisses after 4 s |
| Autostart | Registry run key, toggleable from the tray menu |
| Active query | IN9 V3 is polled with a 33-byte output report (`0x10` device info) |
| Legacy protocols | IN10 passive push and legacy IN9 Feature Report polling are retained |
| Zero dependencies | Single exe, no .NET SDK and no runtime install (uses the built-in .NET Framework) |

### Tray states

| State | Icon | Tooltip |
|-------|------|---------|
| Online | Filled by charge | `INPHIC 67% (2500ms refresh)` |
| Offline | Entirely greyed out | `INPHIC: disconnected / powered off (last 67%, 21:12:03)` |
| Not detected | Greyed out | `INPHIC: no device detected` |

---

## Quick start

1. Get `InphicBatteryTray.exe` (use the build in the repository, or build it as described under [Build](#build))
2. Put it somewhere permanent (e.g. `%LOCALAPPDATA%\InphicBatteryTray\`)
3. Double-click to run; the tray icon appears
4. Right-click the icon → tick **开机自动启动** (Start with Windows)

> Do not put it inside the vendor software's install folder — uninstalling or updating the vendor app may delete it.

---

## Usage

| Action | Effect |
|--------|--------|
| Hover | Shows charge level and refresh interval |
| Right-click | Menu: details / rescan devices / autostart / exit |
| Double-click | Detail popup (connection type, hardware ID, protocol, battery, charging state, refresh interval) |
| Click popup | Closes immediately; otherwise it fades after 4 s and stays while the cursor is over it |

---

## IN9 V3 protocol (measured)

The V3 receiver is completely different from legacy IN9: it exposes **no Feature report**. The host must send a query as a 33-byte output report and read the reply from input reports.

### Transport

| Item | Value |
|---|---|
| Device | `249A:5C2F`, `MI_02`, product `Wireless-Receiver` |
| Report lengths | input 33 / output 33 / Feature 0 |
| Open mode | `CreateFileW` + `FILE_FLAG_OVERLAPPED`, shared read/write, can coexist with the vendor app |
| Poll interval | 2.5 s |
| Read timeout | 300 ms per frame, up to 8 frames per poll |

### Query frame (33-byte output report)

```
00 10 00 00 ... 00
│  └─ query ID: 0x10 = device info (contains battery)
└──── Report ID: 0x00
```

- **One query at a time**: back-to-back writes return a NACK `00 <id> 02 FF`, so the app sends one query per poll and waits for the reply.
- Measured reply latency: about 2 ms.

### Reply frame

```
00 10 00 01 0B 4D 39 31 30 84 01 31 01 01 64 01 ... 04
│  │  │  │  └ len=11    └──────── payload ────────┘  └ checksum
│  │  │  └ mode = 0x01 (reply)
│  │  └ status = 0x00
│  └ query ID echo
└──── Report ID
```

| Offset | Meaning |
|---|---|
| `resp[1]` | query ID echo |
| `resp[2]` | `0x00` OK; `0x02` with `resp[3]=0xFF` is a NACK |
| `resp[3]` | `0x01` = reply |
| `resp[4]` | payload length |
| `resp[5..]` | payload |
| last byte | **checksum = low 8 bits of the payload byte sum** |

Measured `0x10` payload layout (11 bytes):

| payload index | absolute offset | Meaning |
|---|---|---|
| `[0..3]` | 5–8 | model ID, e.g. `4D 39 31 30` → `"M910"` |
| `[8]` | 13 | **charging flag**: `0` not charging, `1` charging |
| `[9]` | 14 | **battery level**: 0–100, integer percent |

### Unsolicited push

The mouse sends a 33-byte frame when the charge cable is plugged/unplugged or the receiver reconnects:

```
00 C0 <a> <battery> <charging> 00 ...
```

- `<a>` is **not** the charging state (it can be 1 while not charging) — do not use it to decide charging.
- `resp[3]` is the battery level (0–100), `resp[4]` is the charging flag (0 not charging / 1 charging).

### The "100% while charging" quirk

Measured firmware reports a **fixed `0x64` (100%)** while charging — in both the `0x10` reply and the `0xC0` push. Displaying it verbatim makes the tray jump to 100% the moment the cable is plugged in.

What the app does:

1. Detect charging from `payload[8]` of the `0x10` reply or `resp[4]` of the push;
2. While charging, if the reported level is 100%, keep showing the **last real level seen before plugging in**;
3. Cache that level in `%LOCALAPPDATA%\InphicBatteryTray\last_battery.txt`, so starting the app mid-charge still shows a real value;
4. After unplugging, the device reports the real level again and the display updates.

> Limitation: the device simply does not provide a real level while charging. If the mouse was at 100% when plugged in, 100% is correct; if the cache is old, the app shows the last recorded value.

### Legacy protocols (not verified in this repository)

- **IN10**: the device pushes a 5-byte Input Report every ~2 s (Report ID `0x03`, subcommand `0x40`, battery in byte 5).
- **Legacy IN9**: 33-byte Feature Report (`0x0C`); the host sends `0x20` to read battery, which is at `buf[0x12]`.

Both frame formats come from the upstream projects and were not re-tested on their hardware here. The V3 receiver has no Feature report, so the legacy `0x0C` query cannot work on it.

---

## Build

Only the .NET Framework compiler that ships with Windows is needed — **no SDK install**:

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /target:winexe /platform:x64 `
    /win32icon:assets\app.ico `
    /resource:assets\MouseIcon.png,MouseIcon.png `
    /out:InphicBatteryTray.exe `
    /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    src\InphicBatteryTray.cs src\AssemblyInfo.cs
```

Or simply:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

### Repository layout

```
InphicBatteryTray/
├─ README.md                    Chinese docs
├─ README.en.md                 English docs
├─ LICENSE                      MIT
├─ .gitignore
├─ build.ps1                    one-step build
├─ InphicBatteryTray.exe        build output (single file)
├─ src/
│  ├─ InphicBatteryTray.cs      all logic (single file)
│  └─ AssemblyInfo.cs           version / author info
└─ assets/
   ├─ app.ico                   application icon
   ├─ MouseIcon.png             tray icon source (256×256)
   ├─ a1.png                    tray screenshot
   └─ preview-levels.png        charge-level preview
```

---

## Privacy and read-only behaviour

- ✅ Reads HID metadata: VID/PID, product string, report lengths
- ✅ IN9 V3: sends a read-only `0x10` device-info query and reads battery/charging bytes
- ✅ IN10 / legacy IN9: passive listening, or a read-only battery query
- ❌ Never reads any `Usage Page 0x0001` (Generic Desktop) interface — that is the keyboard/mouse proper, where keystrokes and movement live
- ❌ Never changes DPI, lighting, button mappings or any other device setting
- ❌ No network access, no telemetry
- 📝 Only writes: `%LOCALAPPDATA%\InphicBatteryTray\last_battery.txt` (local level cache) and the diagnostic log `tray.log` next to the executable

---

## Known limitations

1. **Verified only on the INPHIC IN9 V3 (`249A:5C2F`)**. Other models and firmware revisions are untested; a different protocol may not read at all.
2. **Battery resolution is 1%** at the hardware level; there are no decimals, voltage or health fields.
3. **No real battery level while charging**: the firmware reports 100%, so the app can only show the last recorded value (see above).
4. **Offline is a timeout guess** (8 s): the device sends no "goodbye" on power-off, and a short sleep may also be misreported as offline.
5. Fine detail is lost at 16×16 tray icon sizes — the physical limit of the notification area.

### Battery not detected?

The app writes `tray.log` next to the executable by default (rotated at 512 KB), recording enumerated interfaces, raw send/receive bytes and parse results. You can also set the `INPHIC_TRAY_LOG` environment variable or pass `--log=<path>`.

Attach that log to an issue and the cause is usually obvious.

---

## Credits

- [MRXie-zf/InphicBatteryTray-IN9](https://github.com/MRXie-zf/InphicBatteryTray-IN9) — basis for the IN10 / legacy IN9 support and tray rendering.
- [RuiShu1024/InphicMouse](https://github.com/RuiShu1024/InphicMouse) — an earlier protocol reconstruction of the same vendor's IN9 series.
- [Geovane2dd/Ajazzy](https://github.com/Geovane2dd/Ajazzy) — its reverse-engineering notes document the `0x10` reply and `0xC0` push format of the same 2.4G receiver chip, which was very helpful for understanding the V3 protocol.

---

## Disclaimer

Unofficial third-party tool, not affiliated with INPHIC.

Protocol details were obtained by live capture and interoperability analysis, for the sole purpose of reading battery level. The program does not modify device settings. Use at your own risk.

---

## License

[MIT](LICENSE)
