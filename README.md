# Smart-X Ingestion Gateway

Smart-X Ingestion Gateway is a high-throughput IoT telemetry ingestion pipeline paired with a browser-based management console. Sensor readings are accepted through a REST API, routed through purpose-built in-memory data structures, and processed by a background worker that always handles critical readings first. Operators use the console to monitor live telemetry, resolve alerts, issue manual overrides, and act on automated insights.

## Architecture and Tech Stack

The solution (`SmartX.Gateway.slnx`) contains three projects:

| Project | Type | Responsibility |
| --- | --- | --- |
| `SmartX.Gateway` | ASP.NET Core Minimal API (Server) | Hosts the REST endpoints, the ingestion service, the background telemetry processor, and serves the Blazor client. |
| `SmartX.Gateway.Client` | Blazor WebAssembly (Client) | The management console: Ingestion dashboard, Command Stream, and Topology pages. |
| `SmartX.Gateway.Shared` | Class Library (Shared) | Models, data structures, deployment validation, and the Predictive Action Engine, shared by server and client. |

Technologies:

- .NET 8 (all three projects target `net8.0`)
- ASP.NET Core Minimal APIs with a hosted `BackgroundService`
- Blazor WebAssembly with interactive WebAssembly render mode
- "Organic Precision", a custom CSS design system (`wwwroot/css/organic-theme.css`) with a warm linen and terracotta palette, Fraunces and JetBrains Mono typography, and reduced-motion support. No CSS framework is used.

## Core Features

### Data Structures

Each structure lives in `SmartX.Gateway.Shared/DataStructures/` and is thread-safe, since the API and the background processor access them concurrently.

| Structure | Class | Purpose |
| --- | --- | --- |
| `Queue<T>` | `TelemetryMessageQueue` | Standard FIFO processing of normal telemetry readings. |
| `PriorityQueue` (min-heap) | `CriticalAlertPriorityQueue` | Critical readings bypass the standard queue and are processed by severity before any normal telemetry. |
| `Stack<T>` | `CommandHistoryStack` | LIFO history of manual override commands, used to undo the most recent command. |
| `Dictionary<TKey, TValue>` | `DeviceRegistry` | O(1) lookup of registered sensors by ID. |
| `SortedDictionary<TKey, TValue>` | `SortedTelemetryLog` | Per-sensor telemetry history kept in chronological order. |
| `HashSet<T>` | `ActiveAlertSet` | Deduplicates active alerts so a sensor raises at most one open alert. |

Readings outside the 10 to 90 range are treated as critical. Readings outside 5 to 95 receive the highest priority.

### Predictive Action Engine

`PredictiveActionEngine` analyses a rolling 24-hour window of operator searches, critical sensor events, and issued commands to produce "Automated Insights":

- **Repeated critical readings:** a device with three or more critical readings in the last five minutes is flagged with an "Investigate Device" suggestion before anyone searches for it.
- **Search patterns:** when an operator searches for a device (by full MAC address or its last six hex digits) within five minutes of that device raising a critical reading, the engine records the pattern. After three or more distinct searches, it suggests the most recent command issued to that device, or "Investigate Device" if none exists.
- **Confidence scoring:** confidence rises with the number of matching searches (0.6 for three, 0.75 for four, 0.9 for five or more). Insights are deduplicated per device and action, then returned in descending order of confidence.

### Real-Time Command Stream

The Command Stream page polls the API every three seconds and presents:

- **Incoming Telemetry:** a live feed of readings. The server generates a synthetic reading every three seconds for a random registered sensor so the feed stays active during demonstrations.
- **Manual Override:** select a device (with search and category filters) and issue Open Valve, Close Valve, Toggle Relay, or Restart Sensor commands. "Undo Last Command" pops the most recent command from the history stack.
- **Active Alerts:** open critical alerts, each of which can be resolved.
- **Automated Insights:** suggestions from the Predictive Action Engine with their confidence and reasoning.

### API Reference

| Method | Route | Description |
| --- | --- | --- |
| GET | `/api/sensors` | List registered sensors. |
| POST | `/api/sensors/register` | Register a sensor. |
| POST | `/api/sensors/{id}/telemetry` | Submit a batch of telemetry readings. |
| POST | `/api/sensors/seed/{count}` | Register mock sensors with sample telemetry. |
| POST | `/api/deployment/validate` | Validate a deployment topology node. |
| POST | `/api/commands/issue` | Issue a manual override command. |
| POST | `/api/commands/undo` | Undo the most recent command. |
| GET | `/api/commands/history` | List command history. |
| GET | `/api/alerts/active` | List active alerts. |
| POST | `/api/alerts/{sensorId}/resolve` | Resolve a sensor's alert. |
| GET | `/api/telemetry/{sensorId}/log` | Chronological telemetry log for a sensor. |
| POST | `/api/insights/search` | Record an operator search for pattern analysis. |
| GET | `/api/insights/suggested` | Get suggested actions. |

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022, version 17.13 or later (required to open the `.slnx` solution format)

### Setup

1. Clone the repository:

   ```bash
   git clone https://github.com/SibleyDean/PROG7312_smartX.git
   cd PROG7312_smartX
   ```

2. Restore dependencies. Restoring the server project also restores the Client and Shared projects it references:

   ```bash
   dotnet restore SmartX.Gateway/SmartX.Gateway/SmartX.Gateway.csproj
   ```

3. Build:

   ```bash
   dotnet build SmartX.Gateway/SmartX.Gateway/SmartX.Gateway.csproj
   ```

4. Run the server, which also hosts the Blazor client:

   ```bash
   dotnet run --project SmartX.Gateway/SmartX.Gateway --launch-profile http
   ```

5. Open `http://localhost:5003` in a browser. The port is set in `SmartX.Gateway/SmartX.Gateway/Properties/launchSettings.json` and may differ if you use another launch profile. The console output shows the active URL.

The commands above target the project file directly because building a `.slnx` file from the command line requires .NET SDK 9.0.200 or later. With a newer SDK installed, `dotnet build SmartX.Gateway.slnx` also works.

To run from Visual Studio instead, open `SmartX.Gateway.slnx`, set `SmartX.Gateway` as the startup project, and press F5.

## Usage

All data is held in memory, so the registry is empty on each start and resets when the server stops.

1. **Seed sensors.** Select **Ingestion** in the navigation bar to open the Telemetry Dashboard, then click **Seed 10 Mock Sensors**. The registry fills with ten devices and the background processor begins generating telemetry.
2. **Inspect a sensor.** Click **View** on any row to see its ten most recent readings.
3. **Open the Command Stream.** Select **Command Stream** in the navigation bar. New readings appear in the Incoming Telemetry feed every few seconds, and critical readings show up under Active Alerts.
4. **Issue and undo commands.** In Manual Override, choose a device, click one of the command buttons, then click **Undo Last Command** to reverse it.
5. **Trigger an insight.** Wait for a device to raise an alert, then search for it in the Manual Override device search using its full MAC address or last six hex digits. Clear the field and enter the same search again until you have searched three or more times within five minutes of the alert. A suggestion appears under Automated Insights on the next refresh. Devices that raise three critical readings within five minutes are flagged automatically, so insights often appear right after seeding.
6. **Resolve alerts.** Click **Resolve** next to an alert to clear it.
