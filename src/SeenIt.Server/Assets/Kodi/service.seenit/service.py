# SeenIt for Kodi: tells SeenIt what you're watching. Finished items (past your
# "counts as watched" threshold in SeenIt's settings) become plays.
import json
import threading
import urllib.request

import xbmc
import xbmcaddon

ADDON = xbmcaddon.Addon()


def setting(key):
    return ADDON.getSetting(key).strip()


def rpc(method, params):
    reply = json.loads(xbmc.executeJSONRPC(json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params})))
    return reply.get("result") or {}


def send(payload):
    server, token = setting("server").rstrip("/"), setting("token")
    if not server or not token:
        return
    url = server + "/api/kodi/webhook/" + token

    def post():
        try:
            req = urllib.request.Request(url, data=json.dumps(payload).encode("utf-8"), method="POST",
                                         headers={"Content-Type": "application/json", "User-Agent": "SeenIt-Kodi/1.0"})
            urllib.request.urlopen(req, timeout=15).read()
        except Exception as e:  # never break playback over this
            xbmc.log("SeenIt: couldn't reach %s: %s" % (server, e), xbmc.LOGWARNING)

    threading.Thread(target=post, daemon=True).start()


def ids(uniqueid):
    return {k: str(v) for k, v in (uniqueid or {}).items() if k in ("tmdb", "imdb", "tvdb") and v}


def playing_item():
    """The library item on the video player, or None (files outside the library can't be matched reliably)."""
    players = rpc("Player.GetActivePlayers", {})
    player_id = next((p["playerid"] for p in players if p.get("type") == "video"), None)
    if player_id is None:
        return None
    item = rpc("Player.GetItem", {"playerid": player_id,
                                  "properties": ["title", "year", "showtitle", "season", "episode", "uniqueid", "tvshowid"]}).get("item") or {}
    kind = item.get("type")
    if kind not in ("movie", "episode"):
        return None
    out = {"type": kind, "title": item.get("title"), "year": item.get("year"), "ids": ids(item.get("uniqueid"))}
    if kind == "episode":
        out.update(showtitle=item.get("showtitle"), season=item.get("season"), episode=item.get("episode"))
        if item.get("tvshowid", -1) >= 0:
            show = rpc("VideoLibrary.GetTVShowDetails", {"tvshowid": item["tvshowid"], "properties": ["uniqueid"]}).get("tvshowdetails") or {}
            out["show_ids"] = ids(show.get("uniqueid"))
    return out


class Player(xbmc.Player):
    def __init__(self):
        super().__init__()
        self.item = None
        self.position = 0.0
        self.duration = 0.0

    def refresh(self):
        try:
            if self.item and self.isPlayingVideo():
                self.position = self.getTime()
                self.duration = self.getTotalTime() or self.duration
        except RuntimeError:
            pass

    def notify(self, event, position=None):
        if self.item:
            send(dict(self.item, event=event, duration=self.duration,
                      position=self.position if position is None else position))

    def onAVStarted(self):
        self.item = playing_item()
        self.position = self.duration = 0.0
        self.refresh()
        self.notify("start")

    def onPlayBackPaused(self):
        self.refresh()
        self.notify("pause")

    def onPlayBackResumed(self):
        self.refresh()
        self.notify("start")

    def onPlayBackSeek(self, time, offset):
        self.refresh()

    def onPlayBackStopped(self):
        self.notify("stop")
        self.item = None

    def onPlayBackEnded(self):
        self.notify("end", position=self.duration)
        self.item = None


if __name__ == "__main__":
    monitor = xbmc.Monitor()
    player = Player()
    while not monitor.abortRequested():
        if monitor.waitForAbort(5):
            break
        player.refresh()
