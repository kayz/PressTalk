# FunASR Streaming Protocol

PressTalk uses a long-lived Python subprocess and exchanges one JSON document per line on `stdin` / `stdout`.

## Runtime Lifecycle

1. C# starts `funasr_runtime.py`.
2. C# sends `ping`.
3. Optional: C# sends `preload`.
4. For each dictation session:
   - `start_streaming_session`
   - zero or more `push_audio_chunk`
   - `end_streaming_session`
5. On app shutdown, C# sends `shutdown`.

## Request Envelope

Every request includes:

```json
{
  "request_id": "guid",
  "action": "..."
}
```

## Actions

### `ping`

Request:

```json
{"request_id":"...", "action":"ping"}
```

Response:

```json
{"ok":true, "request_id":"...", "runtime_version":"funasr-streaming-runtime-1"}
```

### `preload`

Request:

```json
{
  "request_id":"...",
  "action":"preload",
  "include_speaker_diarization": true
}
```

Response fields:

- `duration_ms`
- `speaker_loaded`

### `start_streaming_session`

Request:

```json
{
  "request_id":"...",
  "action":"start_streaming_session",
  "session_id":"...",
  "language":"auto",
  "hotwords":["PressTalk","FunASR"],
  "enable_speaker_diarization": true
}
```

### `push_audio_chunk`

Request:

```json
{
  "request_id":"...",
  "action":"push_audio_chunk",
  "session_id":"...",
  "sample_rate":16000,
  "audio_base64":"<pcm16le-base64>"
}
```

Response fields:

- `preview_text`: current preview text
- `confirmed_text`: cumulative confirmed text
- `delta_text`: newly confirmed text since last push
- `is_final`
- `duration_ms`
- `speaker_segments`: empty during live streaming

### `end_streaming_session`

Request:

```json
{
  "request_id":"...",
  "action":"end_streaming_session",
  "session_id":"..."
}
```

Response fields:

- `preview_text`
- `confirmed_text`
- `delta_text`
- `is_final: true`
- `speaker_segments`

## Speaker Segment Schema

```json
{
  "speaker_id":"speaker-1",
  "text":"...",
  "start_ms":0,
  "end_ms":1200
}
```

## Error Response

```json
{
  "ok":false,
  "request_id":"...",
  "error":"message"
}
```
