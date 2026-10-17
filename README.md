## its backend, what did you expect

## Kiosk real-time updates

Connect a SignalR client to `/hubs/queue` and invoke `JoinKiosk()` after connecting. Joined kiosk connections receive:

- `SchoolStatusChanged` with a `SystemStatusDto` whenever the school state, break, music availability, or current track changes.
- `NowPlayingChanged` with a `PlayerStatusDto` whenever playback starts, pauses, resumes, stops, ends, or encounters an error.

Kiosk events are sent only to connections in the `Kiosk` group. Existing queue hub methods and events remain available.
