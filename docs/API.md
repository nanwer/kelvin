# Scripting Kelvin

Kelvin can be driven from scripts on the same PC (local HTTP API) or from your smart home (MQTT,
with Home Assistant discovery).

## Local HTTP API

The Kelvin service listens on **`http://127.0.0.1:5577`**, on this PC only. It's never reachable
from the network.

- `GET` requests need nothing special.
- Every request that changes something (`PUT`, `POST`, `DELETE`) needs the header **`X-Kelvin: 1`**.
  Browsers can't add that header across sites, so a web page can't change your fans or profiles.
- Bodies are JSON. Names are camelCase.

### Reading

| Request | Returns |
|---|---|
| `GET /api/state` | Everything the app shows: sensors, fans, profiles, lighting, alerts, health |
| `GET /api/report` | The hardware report (Markdown) |
| `WebSocket /api/live` | The same as `/api/state`, pushed 4 times a second |

### Profiles

| Request | Body | Does |
|---|---|---|
| `PUT /api/profiles/active` | `{"id":"gaming"}` | Switch profile (holds until the automatic situation changes) |
| `PUT /api/profiles/automation` | the `automation` object from `/api/state` | Change automatic switching |
| `POST /api/profiles` | `{"name":"Streaming"}` | New profile from the current settings |
| `PUT /api/profiles/{id}` | `{"name":"…","fanMode":"quiet"}` | Rename, or set its laptop fan mode |
| `DELETE /api/profiles/{id}` | | Delete (not if automatic switching uses it) |

### Fans, lighting and more

| Request | Body |
|---|---|
| `PUT /api/fans` | one fan's settings (`fanId`, `mode`, `points`, …) |
| `PUT /api/fan-mode` | `{"id":"performance"}` (laptops) |
| `PUT /api/lighting` | the `lighting` object |
| `PUT /api/lighting/screen` | `{ "width", "height", "data" }`: base64 RGB pixels, row by row, at most 64 x 64. Feeds the Screen sync effect, so you can drive it from your own capture tool. |
| `PUT /api/alerts` | the `alertSettings` object |
| `PUT /api/safety` | `{"criticalCpuTemp":90,"criticalGpuTemp":95}` |
| `POST /api/fans/calibrate` | Measure fans (about 30 s) |

### Examples

PowerShell: switch to the Gaming profile, then read the CPU temperature:

```powershell
Invoke-RestMethod -Method Put http://127.0.0.1:5577/api/profiles/active -Headers @{ 'X-Kelvin' = '1' } -ContentType 'application/json' -Body '{"id":"gaming"}'
(Invoke-RestMethod http://127.0.0.1:5577/api/state).sensors | Where-Object { $_.primary -and $_.role -eq 'cpu' } | Select-Object value
```

curl:

```bash
curl -X PUT -H "X-Kelvin: 1" -H "Content-Type: application/json" -d '{"id":"quiet"}' http://127.0.0.1:5577/api/profiles/active
```

## MQTT and Home Assistant

Turn it on in **Settings → Home Assistant** and enter your broker's address. With Home Assistant's
Mosquitto add-on, that's your Home Assistant address, port 1883, and a Home Assistant user.

Kelvin then appears in Home Assistant as a device called **Kelvin (your PC's name)**, with:

- CPU, GPU and motherboard temperature, CPU and GPU load, and each fan's speed;
- **Profile**, a picker that switches profiles, and **Automatic profiles**, an on/off switch;
- **Safety override** and **Temperature alert**, as problem and heat sensors;
- **Laptop fan mode**, on laptops.

### Topics

`<prefix>` is `kelvin` by default; `<device>` is your PC's name in lower case, e.g. `desktop_abc12`.

| Topic | Direction | Payload |
|---|---|---|
| `<prefix>/<device>/state` | Kelvin → | JSON with every value above, every 10 s |
| `<prefix>/<device>/availability` | Kelvin → | `online` / `offline` (retained, offline also as last will) |
| `<prefix>/<device>/profile/set` | → Kelvin | a profile name or id |
| `<prefix>/<device>/automatic/set` | → Kelvin | `ON` / `OFF` |
| `homeassistant/…/kelvin_<device>/…/config` | Kelvin → | Home Assistant discovery (retained) |

Only those two commands exist. Kelvin never accepts fan speeds over MQTT, and you can turn off
profile switching from Home Assistant in Settings. The broker password is stored encrypted with
Windows DPAPI and is never shown again in the app.

### Example automation

Switch to the Night profile when the living-room lights go off:

```yaml
automation:
  - alias: "PC quiet at night"
    trigger:
      - platform: state
        entity_id: light.living_room
        to: "off"
    action:
      - service: select.select_option
        target:
          entity_id: select.kelvin_desktop_abc12_profile
        data:
          option: Night
```
