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
    def do_POST(self):
        n=int(self.headers.get("Content-Length","0")); body=json.loads(self.rfile.read(n) or b"{}")
        Path("artifacts").mkdir(exist_ok=True)
        with LOG.open("a",encoding="utf-8") as f:
            f.write(json.dumps({"path":self.path,"goal":body.get("goal"),"processName":body.get("processName"),"windowTitle":body.get("windowTitle"),"hasImage":str(body.get("image","")).startswith("data:image/"),"controls":body.get("controls",[])},ensure_ascii=False)+"\n")
        if "slow" in str(body.get("goal","")).lower(): time.sleep(1.5)
        target=next((x for x in body.get("controls",[]) if x.get("automationId")=="SmokeButton" or x.get("name")=="Open test target"),None)
        if not target:
            self.sendj({"status":"clarify","action":"none","instruction":"","question":"対象を確認してください。","targetId":None,"key":None,"confidence":.9,"x":0,"y":0,"width":0,"height":0}); return
        self.sendj({"status":"target","action":"left_click","instruction":"「Open test target」を1回クリックしてください。","question":None,"targetId":target["id"],"key":None,"confidence":.99,"x":target["x"],"y":target["y"],"width":target["width"],"height":target["height"]})
if __name__=="__main__": ThreadingHTTPServer(("127.0.0.1",8766),H).serve_forever()
