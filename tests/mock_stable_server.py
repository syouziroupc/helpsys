import json, time
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG=Path("artifacts/stable-mock-requests.jsonl")
class H(BaseHTTPRequestHandler):
    protocol_version="HTTP/1.1"
    def log_message(self,*a): pass
    def sendj(self,obj,status=200):
        raw=json.dumps(obj,ensure_ascii=False).encode()
        self.send_response(status); self.send_header("Content-Type","application/json"); self.send_header("Content-Length",str(len(raw))); self.end_headers(); self.wfile.write(raw)
    def read_body(self):
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            chunks=[]
            while True:
                line=self.rfile.readline().strip()
                if not line: continue
                size=int(line.split(b";",1)[0],16)
                if size==0:
                    while self.rfile.readline() not in (b"\r\n",b"\n",b""): pass
                    break
                chunks.append(self.rfile.read(size))
                if self.rfile.read(2)!=b"\r\n": raise ValueError("invalid chunk")
            return b"".join(chunks)
        n=int(self.headers.get("Content-Length","0"))
        return self.rfile.read(n) if n else b""

    def do_GET(self):
        if self.path.startswith("/release"):
            time.sleep(5)
            self.sendj({"assets":[]})
            return
        self.sendj({"error":"not_found"},404)

    def do_POST(self):
        body=json.loads(self.read_body() or b"{}")
        Path("artifacts").mkdir(exist_ok=True)
        with LOG.open("a",encoding="utf-8") as f:
            f.write(json.dumps({"path":self.path,"goal":body.get("goal"),"processName":body.get("processName"),"windowTitle":body.get("windowTitle"),"hasImage":str(body.get("image","")).startswith("data:image/"),"controls":body.get("controls",[])},ensure_ascii=False)+"\n")
        if "slow" in str(body.get("goal","")).lower(): time.sleep(1.5)
        target=next((x for x in body.get("controls",[]) if x.get("automationId") in ("SmokeButton","StaleButton") or x.get("name") in ("Open test target","Stale action")),None)
        if not target:
            self.sendj({"status":"clarify","action":"none","instruction":"","question":"対象を確認してください。","targetId":None,"key":None,"confidence":.9,"x":0,"y":0,"width":0,"height":0}); return
        label=target.get("name") or "target"
        visual = "visual" in str(body.get("goal","")).lower()
        self.sendj({"status":"target","action":"left_click","instruction":f"「{label}」を1回クリックしてください。","question":None,"targetId":None if visual else target["id"],"key":None,"confidence":.99,"x":target["x"],"y":target["y"],"width":target["width"],"height":target["height"]})
if __name__=="__main__": ThreadingHTTPServer(("127.0.0.1",8766),H).serve_forever()
