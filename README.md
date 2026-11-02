# Smart-X Ingestion Gateway

Smart-X Ingestion Gateway is a high-throughput IoT telemetry ingestion pipeline paired with a browser-based management console. Sensor readings are accepted through a REST API, routed through purpose-built in-memory data structures, and processed by a background worker that always handles critical readings first. Operators use the console to monitor live telemetry, acknowledge alerts, issue manual overrides, act on automated insights, and see how every sensor connects to the gateway.

## Architecture and Tech Stack

The solution (`SmartX.Gateway.slnx`) contains four projects:

| Project | Type | Responsibility |
| --- | --- | --- |
| `SmartX.Gateway` | ASP.NET Core Minimal API (Server) | Hosts the REST endpoints, the ingestion service, the mesh network service, the background telemetry processor, and serves the Blazor client. |
| `SmartX.Gateway.Client` | Blazor WebAssembly (Client) | The management console: Home, Sensors, Command Stream and Topology pages. |
| `SmartX.Gateway.Shared` | Class Library (Shared) | Models, data structures, reading thresholds and units (`SensorReadingInfo`), command rules (`CommandCatalog`), search (`SensorSearch`), factory areas, deployment validation, and the Predictive Action Engine, shared by server and client. |
| `SmartX.Gateway.Tests` | xUnit test project | Unit tests for the shared logic and services, API tests through `WebApplicationFactory`, and bUnit component tests. |

Technologies:

- .NET 8 (all projects target `net8.0`)
- ASP.NET Core Minimal APIs with a hosted `BackgroundService`
- Blazor WebAssembly with interactive WebAssembly render mode
- "Organic Precision", a custom CSS design system (`wwwroot/css/organic-theme.css`) with a warm linen and terracotta palette, Fraunces and JetBrains Mono typography, and reduced-motion support. No CSS framework is used.
- xUnit, `Microsoft.AspNetCore.Mvc.Testing` and bUnit for tests.

## Core Features

### Data Structures

Each structure lives in `SmartX.Gateway.Shared/DataStructures/` and is thread-safe, since the API and the background processor access them concurrently.

| Structure | Class | Purpose |
| --- | --- | --- |
| `Queue<T>` | `TelemetryMessageQueue` | Standard FIFO processing of normal telemetry readings. |
| `PriorityQueue` (min-heap) | `CriticalAlertPriorityQueue` | Warning and critical readings bypass the standard queue and are processed by severity before any normal telemetry. |
| `Stack<T>` | `CommandHistoryStack` | LIFO history of manual override commands, used to undo the most recent command. |
| `Dictionary<TKey, TValue>` | `DeviceRegistry` | O(1) lookup of registered sensors by MAC address or ID. |
| `SortedDictionary<TKey, TValue>` | `SortedTelemetryLog` | Telemetry log kept in chronological order, capped at the newest 5,000 readings. |
| AVL tree | `TelemetryAvlTree` | Time-indexed readings for fast range queries (the Topology history panel), capped at the newest 5,000 readings. |
| `HashSet<T>` | `ActiveAlertSet` | Deduplicates active alerts so a sensor raises at most one open alert. |
| Weighted graph + Dijkstra | `MeshNetworkGraph` | The mesh network: lowest-cost routes to the gateway, rerouting around offline nodes. |

### Reading status, units and thresholds

`SensorReadingInfo` is the single source of truth for both the gateway and the console:

| Status | Rule | Alert priority |
| --- | --- | --- |
| Critical | below 5 or above 95 | 1 |
| Warning | below 10 or above 90 | 2 |
| Normal | 10 to 90 | none |

Units by sensor type: Environmental = temperature in °C, PowerConsumption = power draw in kW, Actuator = valve position in "% open", Unknown = raw signal in "units".

Each sensor keeps its newest 200 readings.

### Live data simulation

The background processor waits a **random 4-12 seconds** before each new reading, then picks one reporting sensor and continues its random walk from its latest value (slowly pulled back towards normal, with an occasional ~3% fault spike). Live data can be paused and resumed from the Sensors page. Commands affect the simulation straight away: **Restart Sensor** brings the reading back to normal, **Open Valve / Close Valve** move an actuator by 15 percentage points, and undoing a valve command reverses it.

Sensors that are offline (simulated failure) or have no route to the gateway send no readings.

### Mesh sensors and demo sensors

Every sensor has a `Source`:

- **Mesh** - six fixed sensors that belong to the mesh network and exist from start-up (Assembly, Packaging, Boiler Room and Cold Storage).
- **Seeded (Demo)** - created by "Load demo sensors". Locations read `<Area> - Line <1-4>`, and each one joins the mesh router for its area (the router is added if that area has none yet).
- **Manual** - registered through `POST /api/sensors/register`. They also join the mesh: the router for their area if the location starts with a known area, otherwise the router with the fewest sensors.

**Clear all sensors** removes demo and manual sensors only. Mesh sensors are kept but reset (no readings, no alerts), the mesh returns to its base layout with every node online, and the telemetry log, AVL index, command history and insights are cleared. "Load demo sensors" on the Home page, and the first getting-started step, depend on whether any demo or manual sensors exist.

The factory areas are Assembly, Packaging, Boiler Room, Cold Storage, Paint Shop, Loading Bay and Press Shop (`FactoryAreas.All`).

### Predictive Action Engine

`PredictiveActionEngine` analyses a rolling 24-hour window of operator searches, warning or critical sensor events, and issued commands to produce "Automated Insights":

- **Repeated bad readings:** a device with three or more warning or critical readings in the last five minutes gets an "Investigate Device" suggestion before anyone searches for it.
- **Search patterns:** when an operator searches for a device (by full MAC address or its last six hex digits) within five minutes of that device raising a bad reading, the engine records the pattern. After three or more distinct searches it suggests the most recent command issued to that device, or "Investigate Device" if none exists.
- **Confidence scoring:** confidence rises with the number of matching searches (0.6 for three, 0.75 for four, 0.9 for five or more). The console shows it as High (80% and up), Medium (65% and up) or Low.

### Console pages

- **Home** - a plain-language summary (sensor count, open alerts, readings in the last minute), a three-step getting-started guide, and "Load demo sensors".
- **Sensors** - the registry with search, a category filter and sortable columns (latest reading, status, source tag), a detail panel with a trend sparkline, Refresh / Auto-refresh, pause and resume of live data, and "Clear all sensors".
- **Command Stream** - a health summary bar (normal / warning / critical / waiting / connection problems), the live feed (with Pause), Manual Override (only the commands that fit the device type, recent commands, Undo), Active Alerts (Send command, Acknowledge, Acknowledge all) and Automated Insights (Apply, Dismiss). `command-stream?device=<sensorId>` preselects a device.
- **Topology** - the mesh map: gateway, one router per area, and sensors fanned out around their router. Nodes show their status with colour and an icon (Normal, Warning, Critical, No data, Offline, No route), open alerts get a ▲ badge, and routers and the gateway roll up "N issues downstream". Select a node for its details, latest reading, sparkline, alert, route and cost (with "Rerouted via ..." when a node is offline), a failure toggle and its 2-hour history. Also: Find on map, Show problems only, a Map / List view (the list is used automatically on narrow screens), Pause updates, and **Validate network**, which checks the live Gateway > Router > Sensor tree and names every failing node.

### API Reference

All `/api` responses are sent with `Cache-Control: no-store`.

| Method | Route | Description |
| --- | --- | --- |
| GET | `/api/sensors` | List registered sensors. Optional `?readings=N` returns only each sensor's newest N readings. |
| POST | `/api/sensors/register` | Register a sensor (Source = Manual; it joins the mesh). 400 for a duplicate MAC. |
| POST | `/api/sensors/{id}/telemetry` | Submit a batch of telemetry readings. |
| POST | `/api/sensors/seed/{count}` | Register `count` demo sensors with sample telemetry. `count` must be 1-50 (400 otherwise). |
| POST | `/api/sensors/clear` | Remove demo and manual sensors; reset mesh sensors, alerts, logs, command history, insights and offline nodes. |
| GET | `/api/summary` | Counts for the console: sensors, demo sensors, open alerts, readings in the last minute, total readings, acknowledged alerts, generation paused, and connection problems (with plain-text descriptions). |
| GET | `/api/simulation` | Whether live data generation is paused. |
| POST | `/api/simulation/pause` | Pause live data generation. |
| POST | `/api/simulation/resume` | Resume live data generation. |
| POST | `/api/deployment/validate` | Validate a deployment tree (true / false). |
| POST | `/api/deployment/validate/details` | Validate a deployment tree and list every failing node with its reason. |
| POST | `/api/commands/issue` | Issue a manual override command. 400 for an unknown command, or one that does not fit the device type (valves on actuators, relays on power meters, restart on anything). |
| POST | `/api/commands/undo` | Undo the most recent command. |
| GET | `/api/commands/history` | List command history, newest first. |
| GET | `/api/alerts/active` | List the IDs of sensors with an open alert. |
| GET | `/api/alerts/details` | The triggering value, priority and time of each open alert. |
| POST | `/api/alerts/{sensorId}/resolve` | Acknowledge a sensor's alert (cleared until its next bad reading). |
| POST | `/api/alerts/acknowledge-all` | Acknowledge every open alert. |
| GET | `/api/telemetry/{sensorId}/log` | Chronological telemetry log for a sensor. |
| GET | `/api/telemetry/{sensorId}/range?start=&end=` | A sensor's readings in a time range (UTC), from the AVL index. |
| POST | `/api/insights/search` | Record an operator search for pattern analysis. |
| GET | `/api/insights/suggested` | Get suggested actions. |
| GET | `/api/topology` | The mesh: nodes (with role, area, location, sensor ID, category, source, latest reading, route state) and links. |
| GET | `/api/topology/problems` | Offline nodes and nodes with no route to the gateway. |
| GET | `/api/topology/route/{mac}` | A node's route to the gateway, its normal (baseline) route, and the path cost. |
| POST | `/api/topology/fail/{mac}` | Take a node offline (simulated failure). |
| POST | `/api/topology/restore/{mac}` | Bring a node back online. |
| POST | `/api/topology/restore-all` | Bring every node back online. |

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

### Running the tests

```bash
dotnet test SmartX.Gateway.Tests/SmartX.Gateway.Tests.csproj
```

(or `dotnet test SmartX.Gateway.slnx` with SDK 9.0.200 or later). API tests host the app in memory with the background generator removed, and random values come from seeded `Random` instances, so results are repeatable.

## Usage

All data is held in memory and resets when the server stops. On start-up only the six mesh sensors exist.

1. **Load demo sensors.** On the Home page (or the Sensors page) click **Load demo sensors**. Ten demo sensors appear, each with a short history, and live readings start arriving every 4-12 seconds.
2. **Inspect a sensor.** On the **Sensors** page, click **View** on any row to see its trend and ten most recent readings. Sort by any column, search, or filter by category.
3. **Open the Command Stream.** The health bar shows at a glance how many sensors are normal, warning or critical. New readings appear in the Incoming Telemetry feed, and warning or critical readings show up under Active Alerts.
4. **Issue and undo commands.** Click **Send command** on an alert (or pick a device in Manual Override), choose one of the commands offered for that device type, and watch the next reading in the feed. **Undo** states what it will undo.
5. **Trigger an insight.** Wait for a device to raise an alert, then search for it in the Manual Override device search (separators and case do not matter) three or more times within five minutes of the alert. A suggestion appears under Automated Insights; click **Apply** to send it or **Dismiss** to hide it.
6. **Acknowledge alerts.** Click **Acknowledge** (or **Acknowledge all**). An alert stays cleared until that sensor's next warning or critical reading.
7. **Explore the Topology.** Select a router and click **Take offline**: its sensors reroute through a neighbouring router ("Rerouted via ..."). Take the neighbour offline too and they show **No route** and stop reporting. The Command Stream health bar and the nav badge list these connection problems until you bring the nodes back. Use **Validate network** to check the deployment tree.
8. **Start over.** On the Sensors page click **Clear all sensors** and confirm.
