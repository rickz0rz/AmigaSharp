#!/usr/bin/env python3
"""An example coordinator for the Prevue stream of AmigaSharp.

It plays a cycle, again and again:

1. A video behind the grid. The top half stays clear, so the video shows there. The music is quiet.
2. A pause over black. The music is loud. For each title, a promo shows for a time. Then a logo shows.

Start the stream with the genlock queue, for example:

    ./run-prevue.sh --drive /path/to/drive --headless --stream 8091 --genlock-control --audio /path/to/music

Then run this script on any computer that can connect to the stream port:

    python3 prevue-coordinator.py --url http://localhost:8091 --video /videos/prevue-1993.mp4 \\
        --title Seinfeld --title "Bob's Burgers"

The video path is a path on the computer of the launcher. See docs/orchestration.md.
"""

import argparse
import json
import time
import urllib.request

# ESQ shows the next logo of its rotation about 3 minutes after the last change of the top half. A command more
# often than this keeps the coordinator in control of the top half.
HOLD_SECONDS = 30


def request(url, method="GET", body=None):
    data = json.dumps(body).encode() if body is not None else b""
    req = urllib.request.Request(url, data=data if method != "GET" else None, method=method)
    with urllib.request.urlopen(req, timeout=10) as response:
        return json.load(response)


class Stream:
    def __init__(self, url):
        self.url = url.rstrip("/")

    def get(self, path):
        return request(self.url + path)

    def post(self, path, body=None):
        return request(self.url + path, "POST", body if body is not None else {})

    def ctrl(self, path, body=None):
        """Sends a command on the control line, and waits until Prevue read it (see GET /prevue/state)."""
        self.post("/prevue/ctrl/" + path, body)
        time.sleep(0.5)
        while not self.get("/prevue/state")["lastRequest"].get("read", True) or self.get("/prevue/ctrl")["queued"] > 0:
            time.sleep(0.5)

    def queue(self, item):
        """Adds an item at the end of the genlock queue, and gives its id."""
        return self.post("/genlock/queue", item)["queue"][-1]["id"]

    def current_id(self):
        current = self.get("/genlock")["current"]
        return current["id"] if current else None

    def music(self, volume, fade):
        self.post("/mixer/music", {"volume": volume, "fade": fade})


def hold(stream, seconds, clear):
    """Waits, and sends a command each HOLD_SECONDS so that the logo rotation of ESQ does not start."""
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        time.sleep(min(HOLD_SECONDS, max(0, end - time.monotonic())))
        if clear and time.monotonic() < end:
            stream.ctrl("clear")


def play_video(stream, video):
    print(f"video: {video}", flush=True)
    stream.music(0.3, 2)
    video_id = stream.queue({"source": video})
    # Wait until the video starts, then keep the top half clear until it ends.
    while stream.current_id() != video_id:
        time.sleep(0.5)
    stream.ctrl("clear")
    last_clear = time.monotonic()
    while stream.current_id() == video_id:
        time.sleep(1)
        if time.monotonic() - last_clear >= HOLD_SECONDS:
            stream.ctrl("clear")
            last_clear = time.monotonic()


def show_logo(stream, logos, cycle):
    """Shows the loaded logo, and makes the logo of the next cycle the logo that Prevue loads after it.

    Prevue loads the next logo when it shows a logo, so the script chooses the next logo before the show.
    """
    if logos:
        stream.post("/prevue/logos/next", {"name": logos[(cycle + 1) % len(logos)]})
    loaded = stream.get("/prevue/state")["logos"]["loaded"]
    print(f"logo: {loaded}", flush=True)
    stream.ctrl("logo")


def pause(stream, seconds, titles, promo_seconds, logo_seconds, logos, cycle):
    print(f"pause: {seconds} s", flush=True)
    stream.music(1.0, 3)
    stream.queue({"source": "black", "seconds": seconds})
    end = time.monotonic() + seconds
    for title in titles:
        if time.monotonic() + promo_seconds > end:
            break
        print(f"promo: {title}", flush=True)
        stream.ctrl("promo", {"title": title})
        hold(stream, promo_seconds, clear=False)
    if time.monotonic() + logo_seconds <= end:
        show_logo(stream, logos, cycle)
        hold(stream, logo_seconds, clear=False)
    stream.ctrl("clear")
    hold(stream, max(0, end - time.monotonic()), clear=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", default="http://localhost:8091", help="the stream port of the launcher")
    parser.add_argument("--video", required=True, help="a video file on the computer of the launcher, or a URL")
    parser.add_argument("--title", action="append", default=[], help="a program for a promo (more than one is OK)")
    parser.add_argument("--pause", type=float, default=180, help="the seconds of the pause (default 180)")
    parser.add_argument("--promo-seconds", type=float, default=30, help="the seconds of each promo (default 30)")
    parser.add_argument("--logo-seconds", type=float, default=30, help="the seconds of the logo (default 30)")
    parser.add_argument("--logo", action="append", default=[],
                        help="a logo of LOGO.LST for the pauses, in turn (more than one is OK). Put the first one first "
                             "in LOGO.LST, because the first pause shows the logo that Prevue loaded at its start.")
    parser.add_argument("--cycles", type=int, default=0, help="the number of cycles (default 0: no end)")
    args = parser.parse_args()

    stream = Stream(args.url)
    cycle = 0
    while args.cycles == 0 or cycle < args.cycles:
        play_video(stream, args.video)
        pause(stream, args.pause, args.title, args.promo_seconds, args.logo_seconds, args.logo, cycle)
        cycle += 1


if __name__ == "__main__":
    main()
