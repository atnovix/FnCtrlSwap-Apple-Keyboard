# FnCtrlSwap

Achtergrondtool voor Windows 11 die het **Apple Wireless Keyboard (A1314)** bruikbaar maakt op een Windows-laptop. Geschreven in C# (één bestand, geen dependencies buiten .NET Framework).

## Wat het doet

| Toets | Actie |
|---|---|
| **Fn** | werkt als linker **Ctrl** |
| **Ctrl+Backspace** (dus ook Fn+Backspace) | **Delete** |
| **= + Backspace** ("=" vasthouden naast Backspace) | **Delete** — "=" werkt als vasthoudtoets: een losse tik typt gewoon "=" (pas bij loslaten); auto-repeat van "=" vervalt |
| **Fn+F1 / F2** | helderheid − / + (intern scherm, via WMI) |
| **Fn+F3** | taakweergave (Win+Tab) |
| **Fn+F4** | widgets (Win+W) |
| **Fn+F7 – F12** | vorige, play/pauze, volgende, mute, volume − / + |
| **Eject (⏏)** | opent **Claude Code** in een cmd-venster — in de map die in Verkenner openstaat als dat het voorgrondvenster is, anders in Documenten |

## Hoe het werkt

De Fn- en Eject-toets zitten niet in het normale toetsenbord-report; de tool leest raw HID-reports van het Apple-toetsenbord (report-id `0x11`: bit `0x10` = Fn, bit `0x08` = Eject — techniek uit [uxsoft/AppleWirelessKeyboard](https://github.com/uxsoft/AppleWirelessKeyboard)). Fn ingedrukt → linker-Ctrl injecteren via `SendInput`. Daarnaast draait een low-level keyboard hook voor de Ctrl+Backspace- en Fn+F-combinaties. Het toetsenbord wordt herkend op Apple-VID `05AC` (USB) / `000205AC` (Bluetooth), met elke 3 s een rescan zodat opnieuw verbinden vanzelf werkt.

## Bouwen

In PowerShell 5.1 (let op: de CodeDom-compiler kan alleen C#5-syntax aan):

```powershell
Add-Type -Path FnCtrlSwap.cs -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.Management -OutputAssembly FnCtrlSwap.exe -OutputType WindowsApplication
```

Draait er al een oude versie, stop die eerst: `Stop-Process -Name FnCtrlSwap -Force`

## Installatie / autostart

De tool draait vanuit `%LOCALAPPDATA%\FnCtrlSwap\` en start automatisch mee via een `HKCU\...\Run`-registerwaarde genaamd `FnCtrlSwap`:

```powershell
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\FnCtrlSwap"
# FnCtrlSwap.exe (en .cs) daarheen kopiëren, dan:
Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" FnCtrlSwap "$env:LOCALAPPDATA\FnCtrlSwap\FnCtrlSwap.exe"
```

## Opties

- standaard: volledig onzichtbaar (geen venster, geen tray-icoon)
- `--tray` — toont een tray-icoon met Actief-toggle en Afsluiten
- `--debug` — logt alle binnenkomende HID-reports naar `FnCtrlSwap.log` (log zit altijd naast de exe en wordt boven 512 kB automatisch geleegd)
