# Stream the display as a TV channel

This document is a part of the [README](../README.md). It tells how the launcher streams the display as a TV
channel, with a video behind it and music.

The launcher can stream the display as live HLS video. ffmpeg (from the PATH) encodes it to H.264 at 29.97 pictures
each second, with an AAC audio track. An HTTP server gives the stream and an M3U playlist of one channel:

```sh
CHANNELS_DVR=http://channels-dvr.local:8089 scripts/run-esq.sh --headless --stream 8091 --deinterlace blend \
    --stream-name "Prevue Guide"
```

- `http://<this host>:8091/stream.m3u8` is the stream.
- `http://<this host>:8091/channels.m3u` is a playlist for a custom channel in Channels DVR. Add it as a custom
  channel source of the type M3U playlist, with the address of this host that the server can reach.
- The picture is 4:3 in a 1280 by 720 picture with black bars at the sides. `--stream-4x3` makes a 960 by 720
  picture without bars.
- The sound of the stream is the sound of the Amiga, with the sound of the genlock video and with music. For Prevue,
  the sound of the Amiga is silent.
- `--stream-audio <path>` puts music in the music queue of the stream: an M3U playlist, a text file with one audio
  file on each line, or a directory of audio files. The launcher reads the playlist when it starts. The files play
  in a loop, in the order of the playlist (or of their names in a directory). A path in a playlist can be relative
  to the directory of the playlist. A file that does not play leaves the loop. By default, the music plays while no
  genlock video with sound plays. When a video with sound starts, the music fades out in half a second and stops.
  When the video ends, the music fades in and continues from the same place. See
  [Control the sound](#control-the-sound).
- `--headless` runs without a window until Ctrl-C. The stream also works with a window.
- macOS can ask if the launcher can accept incoming network connections. Accept it, so that the server can connect.
- On Windows, only an administrator can listen for HTTP on all the addresses of the computer, so `--stream` stops
  with "Access is denied". Reserve the port for your account, and let the firewall accept connections to it. Do these
  steps one time, in a PowerShell window that runs as administrator. The example is for port 8091:

  ```powershell
  netsh http add urlacl url=http://*:8091/ user=$env:USERDOMAIN\$env:USERNAME
  netsh advfirewall firewall add rule name="AmigaSharp stream" dir=in action=allow protocol=TCP localport=8091
  ```

The Prevue channel showed its grid over a video: a genlock put the video in each pixel of color 0. `--genlock <file
or URL>` does the same in the stream. A file plays in a loop, and a URL plays live, for example a channel of an
HDHomeRun tuner (`http://<tuner>:5004/auto/v<channel>`):

```sh
CHANNELS_DVR=http://channels-dvr.local:8089 scripts/run-esq.sh --headless --stream 8091 --deinterlace blend \
    --genlock http://hdhomerun.local:5004/auto/v2
```

- The video fills the 4:3 picture, and its sides are cut. An interlaced video is deinterlaced. The video has the
  resolution of the display, about the resolution of NTSC.
- The top half of the screen, and the border at the sides of the grid, show the video. The grid does not use color 0.
- The stream shows each picture of the video once, with the picture of the display of the same moment. So the video
  and the grid both move at an even rate.
- The stream has the sound of the video. The music of `--stream-audio` stops while a video with sound plays.
- If a live source stops, the stream shows the display over black and continues. The launcher starts the source again
  after 2 seconds.

## Queue videos

The genlock has a queue of videos. The HTTP server of the stream changes the queue while the stream runs. With
`--genlock-control` in place of `--genlock`, the stream starts with an empty queue. With no video in the queue, the
display shows over black.

Each video is a file or a URL, with these values in JSON:

| Value | Meaning |
|---|---|
| `source` | A file, a URL, or `black`. This value is necessary. A source with no video, for example a music file, plays over black. `black` is black and silence, for a pause. |
| `seconds` | The time that the video plays. Without it, a file plays to its end, and a URL plays until you skip it. `black` needs it. |
| `loop` | `true` to play a file in a loop. |
| `next` | `true` to put the video first in the queue, not last. |

For example, play a channel for 5 minutes, then a file to its end, then the channel for 10 minutes, then a song over
black:

```sh
curl -X POST http://localhost:8091/genlock/queue -d '[
  {"source": "http://hdhomerun.local:5004/auto/v2", "seconds": 300},
  {"source": "/videos/promo.mp4"},
  {"source": "http://hdhomerun.local:5004/auto/v2", "seconds": 600},
  {"source": "/music/theme.mp3"}
]'
```

These requests control the queue:

| Request | Result |
|---|---|
| `GET /genlock` | Gives the current video and the queue, as JSON. |
| `POST /genlock/queue` | Adds a video, or an array of videos, at the end of the queue. |
| `POST /genlock/next` | Ends the current video. The next video starts, or black shows. |
| `DELETE /genlock/queue` | Removes all the videos from the queue. The current video continues. |
| `DELETE /genlock/queue/<id>` | Removes one video from the queue. `GET /genlock` gives the ids. |
| `POST /genlock/stop` | Removes all the videos from the queue, and ends the current video. |

- A `POST` without data needs `-d ''` in curl, for example `curl -X POST -d '' http://localhost:8091/genlock/next`.
  Without it, the server gives the error 411 (Length Required).
- The time of a video is the time of the stream from its start, also while a live source starts.
- The launcher starts the next video 5 seconds before the current video ends, so the next video starts without black.
  A live source without `seconds` has no known end, so the next video starts with black for a few seconds.
- A file in a request must be on the computer of the launcher.

`--genlock-playlist <file>` starts the stream with the videos of a JSON file, in place of `--genlock` or
`--genlock-control`. The file has the videos of the queue, and `"loop": "all"` to play them in a loop. A relative
file is relative to the directory of the JSON file. For example, play a video, then show the grid over black for 3
minutes, and then start again:

```json
{
  "loop": "all",
  "queue": [
    {"source": "prevue-1993.mp4"},
    {"source": "black", "seconds": 180}
  ]
}
```

The HTTP server then controls the queue, as with `--genlock-control`. The file can also be a JSON array of videos,
with no loop.

Anyone who can connect to the port of the stream can change the queue, and can play any video file that the launcher
can read. Use the stream only on a network that you trust.

## Control the sound

The sound of the stream has three layers:

| Layer | Sound |
|---|---|
| `video` | The sound of the current genlock video. |
| `music` | The music queue. `--stream-audio` fills it when the stream starts. |
| `amiga` | The sound of the audio channels of the Amiga. |

The music queue has the same requests as the genlock queue, at `/music` in place of `/genlock`: `GET /music`,
`POST /music/queue`, `POST /music/next`, `POST /music/stop`, `DELETE /music/queue[/<id>]`. The music queue plays
only the sound of its files. `POST /music -d '{"loop": "all"}'` plays the queue in a loop: a file that ends goes to
the end of the queue again. `"off"` plays each file once. The same request at `/genlock` loops the genlock queue.

These requests control the mixer:

| Request | Result |
|---|---|
| `GET /mixer` | Gives the settings and the level of each layer, and the duck settings, as JSON. |
| `POST /mixer/<layer>` | Changes a layer: `{"volume": 0.5, "muted": false, "fade": 2}`. |
| `POST /mixer/duck` | Changes when and how the music becomes quieter: `{"when": "video-has-sound", "volume": 0.2, "fade": 0.5}`. |

- Each value of a request is optional. The other values do not change.
- `volume` is from 0 to 4 for a layer, and 1 is the normal level. A change goes to the new volume in `fade` seconds.
  `muted` makes the layer silent, also in `fade` seconds.
- The duck makes the music quieter while the genlock video has sound. `volume` is the part of its volume that the
  music keeps: 0 stops it, and it continues from the same place later. `"when": "never"` turns the duck off, for
  example when another program sets the volume of the music itself.
- `level` in `GET /mixer` is the peak level of the layer in the last second, from 0 (silent) to 1 (full scale). A
  program can use it to see that a layer is silent, for example a live channel that lost its sound.

For example, make the music quieter in 3 seconds, and let it play at a fifth of its volume under the videos:

```sh
curl -X POST http://localhost:8091/mixer/music -d '{"volume": 0.3, "fade": 3}'
curl -X POST http://localhost:8091/mixer/duck -d '{"volume": 0.2}'
```

`--deinterlace` sets how the window, the screenshots and the stream show the interlaced display of Prevue:

- `weave` shows the two fields on their rows, as a TV does. Moving content has comb lines. This is the default.
- `bob` shows the last field with each row twice. It has no comb lines, and half the vertical detail.
- `blend` shows the average of the two fields. It has no comb lines, and moving content is a little blurred. It is
  the best mode for a stream.
