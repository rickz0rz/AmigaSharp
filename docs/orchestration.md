# Orchestrate the Prevue stream

The stream of AmigaSharp is a TV channel with parts that change while it plays. The parts are the video behind the
grid, the top half of the screen, and the layers of the sound. A **coordinator** is a program that changes these
parts on a schedule. For example, a script, Home Assistant, or a cron job. It sends HTTP requests to the port of the
stream.

This document tells what each part does, how fast it changes, and how to use the parts together. The README and
[ctrl-line.md](ctrl-line.md) give all the requests.

![A promo in the top half, over the genlock video](prevue-promo-genlock.png)

## The parts

| Part | What shows or plays | Requests |
|------|---------------------|----------|
| Genlock video | The video behind the grid and in the top half. | `/genlock` (queue) |
| Top half | The genlock video, a promo, or a logo. | `/prevue/ctrl`, `/prevue/logos` |
| Grid | The listings. The coordinator does not control it. | none |
| Video sound | The sound of the current genlock video. | `/mixer/video` |
| Music | A queue of audio files. | `/music` (queue), `/mixer/music` |
| Amiga sound | The sound of Prevue itself. | `/mixer/amiga` |

The top half shows one of three things:

- **Video**: the genlock video. The top half of Prevue is transparent.
- **Promo**: the box of a program: call letters, title, next time, and channel. It covers the right or the left half
  of the top of the screen. The coordinator asks for it.
- **Logo**: a picture that covers all the top of the screen, for example "TV Guide sportsview". ESQ shows the logos
  of `LOGO.LST` in a rotation. The coordinator can show the next logo, or remove it.

## Start the stream

Start the stream with an empty genlock queue, so that the coordinator controls the video:

```sh
./run-prevue.sh --drive /path/to/drive --headless --stream 8091 --genlock-control --audio /path/to/music
```

- `--genlock-playlist <file>` starts the queue from a JSON file in place of `--genlock-control`. It is good for a
  fixed schedule without a coordinator. See [A schedule without a coordinator](#a-schedule-without-a-coordinator).
- `--audio` fills the music queue and plays it in a loop.
- The logos come from `LOGO.LST` on the drive. Edit it in the copy of the drive before the stream starts. See
  [Logos](#logos).

The HTTP server starts some seconds before Prevue reads its control line. The requests of `/prevue/ctrl` wait in a
queue until then. A coordinator can start at the same time as the stream.

## Timing

A coordinator must know how fast each part changes:

| Action | Time |
|--------|------|
| A file of the genlock queue starts | At once when it is the next item: the launcher prepares it 5 seconds before. |
| A live URL starts | About 3 to 4 seconds. Black shows before it. |
| A command on the control line | 11 bytes each second. A promo is about 2 seconds. |
| Prevue reads a command | Some seconds more, when its display is not busy. |
| The logo rotation of ESQ | About 3 minutes after the last change of the top half. |
| The next logo is ready | ESQ loads it after a logo shows. 20 seconds was enough in tests. |
| A volume change | The `fade` of the request. |

To send control commands in sequence, wait until `lastRequest.read` in `GET /prevue/state` is `true`. See
[Watch the state](#watch-the-state).

## Watch the state

`GET /prevue/state` tells the coordinator what Prevue does now:

```json
{
  "topHalf": "promo-right",
  "line": {"queued": 0, "inEsq": 0, "idle": true, "commands": 2, "checksumErrors": 0},
  "lastRequest": {"name": "promo", "read": true},
  "logos": {
    "loaded": "Enews.uv",
    "nextLine": 3,
    "shown": "tvgsport.uv",
    "list": [{"line": 1, "path": "Logos/tvgsport.uv", "channel": false}]
  }
}
```

- `topHalf` is what the top half shows: `video`, `promo-right`, `promo-left`, `logo`, or `other`. The launcher reads
  it from the picture, so it is always correct.
- `line` is the state of the control line. `queued` is the bytes in the queue of the launcher, and `inEsq` is the
  bytes that Prevue received and did not read. `idle` is `true` when Prevue read all the commands.
- `lastRequest` is the last request of `/prevue/ctrl`, and `read` is `true` when Prevue read it. For example, after a
  promo request, `read` becomes `true`. Then `topHalf` tells if Prevue found the program: `promo-right` or
  `promo-left` if it found it, and `logo` if not.
- `logos.loaded` is the logo that shows at the next logo command. `logos.nextLine` is the line of `LOGO.LST` that
  Prevue loads after it. `logos.shown` is the last logo that showed.

The launcher knows the variables of Prevue from the listing (`--listing`), or for the known build of ESQ. For another
build of ESQ without a listing, the state has only `topHalf`, the queue of the launcher, and the last request.

## Recipes

### Show a video in the top half

The logo rotation covers the top half about 3 minutes after the last change. To keep the video visible, do one of
these:

- Send `POST /prevue/ctrl/clear` each 30 to 60 seconds while the video plays.
- Or empty `LOGO.LST` in the copy of the drive. Then no logos show, and the promos still work.

### Show a promo

```sh
curl -X POST http://localhost:8091/prevue/ctrl/promo -d '{"title": "Seinfeld", "brush": "AT"}'
```

- Prevue finds the next time of the program in its listings. The promo stays until the next command, or until the
  logo rotation shows a logo.
- If Prevue finds no program, it shows the next logo in place of the promo.
- A promo covers one half of the top of the screen. The genlock video shows in the other half.

### Show a logo

```sh
curl -X POST -d '' http://localhost:8091/prevue/ctrl/logo
```

- Each request shows the loaded logo (`logos.loaded` in `GET /prevue/state`). Then Prevue loads the next line of
  `LOGO.LST`.
- To choose the logo after the loaded one, send `POST /prevue/logos/next` before the logo command:

  ```sh
  curl -X POST http://localhost:8091/prevue/logos/next -d '{"name": "Insider"}'
  curl -X POST -d '' http://localhost:8091/prevue/ctrl/logo
  ```

  The second request shows the loaded logo, and Prevue loads Insider. The next logo command shows Insider. The name
  is the path of the line, its file name, or its file name without the extension. `{"line": 3}` chooses a line by
  its number.
- To show a planned sequence of logos, choose the next logo before each logo command. The first logo is the first line
  of `LOGO.LST`. The rotation of ESQ also shows the loaded logo. Send a command more often than each 3 minutes, and
  the rotation does not start.
- A channel logo (a line without a comma) shows the call letters and the channel number on the picture.

See [Logos](ctrl-line.md#logos) for the format of `LOGO.LST`.

### Music under the videos

- The music becomes quieter while a genlock video with sound plays. `POST /mixer/duck` sets how much quieter.
- `POST /mixer/music` with `volume` and `fade` changes the music slowly, for example louder during a pause.
- `POST /music/queue` adds a song. `{"source": "black", "seconds": 10}` in the music queue is 10 seconds of silence.

### A pause between videos

`{"source": "black", "seconds": 180}` in the genlock queue shows the grid over black for 3 minutes. It has no sound,
so the music plays at its full volume.

### A schedule without a coordinator

A JSON file with `"loop": "all"` repeats a schedule. For example, a video and then 3 minutes over black:

```json
{
  "loop": "all",
  "queue": [
    {"source": "prevue-1993.mp4"},
    {"source": "black", "seconds": 180}
  ]
}
```

Start the stream with `--genlock-playlist <file>`. Relative files are relative to the JSON file. The file controls
only the genlock video. The logos of ESQ rotate, and no promos show.

## Example coordinator

[`scripts/examples/prevue-coordinator.py`](../scripts/examples/prevue-coordinator.py) repeats a cycle:

1. A video plays. The script keeps the top half clear, so the video shows there. The music is quiet.
2. A pause over black. The music is loud. A promo shows for each title, and then a logo.

```sh
python3 scripts/examples/prevue-coordinator.py --url http://localhost:8091 \
    --video /videos/prevue-1993.mp4 --title Seinfeld --title "Bob's Burgers"
```

The script waits until Prevue read each command (`GET /prevue/state`). With `--logo <name>` (more than one), it
shows these logos in turn: before each logo command, it chooses the logo of the next pause.

The script needs only Python 3. The video path is a path on the computer of the launcher. Use `--help` to see the
options. Change the script to make your own schedule.

## Security

Anyone who can connect to the port of the stream can change the channel. That person can also play any video file
that the launcher can read. Use the stream only on a network that you trust.
