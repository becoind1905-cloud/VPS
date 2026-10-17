"""Local API regression checks. Uses isolated files, never contacts Render."""
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time
import urllib.error
import urllib.request

ROOT=Path(__file__).resolve().parents[1]
DATA=ROOT/"artifacts"/"trial-tests"/secrets.token_hex(6)
DATA.mkdir(parents=True)
with socket.socket() as sock:
    sock.bind(("127.0.0.1",0)); PORT=sock.getsockname()[1]
BASE=f"http://127.0.0.1:{PORT}"
KEY=secrets.token_hex(24)
env=dict(os.environ)
env.pop("DATABASE_URL",None)
env.update(EKKO_ADMIN_KEY=KEY,EKKO_DATA_DIR=str(DATA),ASPNETCORE_ENVIRONMENT="Production")
DLL=ROOT/"src/EkkoBatchVideo.AuthServer/bin/Release/net8.0/EkkoBatchVideo.AuthServer.dll"
def api(path, data=None, method=None, admin=False, ip=None):
    headers={"Content-Type":"application/json"}
    if admin: headers["X-Admin-Key"]=KEY
    if ip: headers.update({"X-Forwarded-For":ip,"X-Real-IP":ip})
    req=urllib.request.Request(BASE+path,data=None if data is None else json.dumps(data).encode(),
                               headers=headers,method=method or ("GET" if data is None else "POST"))
    try:
        with urllib.request.urlopen(req,timeout=15) as resp:
            body=resp.read(); return resp.status,json.loads(body) if body else None
    except urllib.error.HTTPError as ex:
        body=ex.read()
        try: body=json.loads(body)
        except Exception: body=None
        return ex.code,body

def start():
    log=open(DATA/"server.log","ab")
    proc=subprocess.Popen(["dotnet",str(DLL),"--urls",BASE],env=env,cwd=ROOT/"src/EkkoBatchVideo.AuthServer",
                          stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
    log.close()
    for _ in range(100):
        if proc.poll() is not None: raise RuntimeError("Server failed; inspect local server.log")
        try:
            if api("/api/admin/users")[0]==401: return proc
        except (OSError,urllib.error.URLError): pass
        time.sleep(.1)
    proc.terminate(); raise RuntimeError("Server did not start")
def stop(proc):
    proc.terminate(); proc.wait(timeout=10)
def machine(n): return hashlib.sha256(str(n).encode()).hexdigest().upper()
def register(name,device,trial,ip="192.0.2.1"):
    return api("/api/auth/register",dict(email=name,password="x",deviceId=device,trialDeviceId=trial),ip=ip)
proc=start()
try:
    before=time.time()
    status,body=register("first","device-a",machine("a"))
    assert status==200 and body["valid"],(status,body)
    from datetime import datetime
    remaining=datetime.fromisoformat(body["expiresAt"].replace("Z","+00:00")).timestamp()-before
    assert 3590<remaining<3610,remaining
    token=body["token"]
    assert api("/api/auth/check",dict(token=token,deviceId="device-a"))[1]["valid"]
    print("PASS registration grants 1 hour; login/check active")
    assert register("second","device-a",machine("a"),"203.0.113.199")[0]==403
    assert register("other-windows-user","changed-login-id",machine("a"),"198.51.100.10")[0]==403
    assert api("/api/auth/register",dict(email="old-client",password="x",deviceId="old"))[0]==400
    print("PASS changed IP/login-user identity cannot get another trial; old client instructed to update")
    assert api("/api/admin/users/first/expiry",dict(hours=-1),admin=True)[0]==200
    assert not api("/api/auth/check",dict(token=token,deviceId="device-a"))[1]["valid"]
    assert api("/api/admin/users/first/reset-device",{},admin=True)[0]==200
    assert api("/api/admin/users/first",method="DELETE",admin=True)[0]==200
    assert register("after-delete","other-id",machine("a"))[0]==403
    assert api("/api/admin/backup-device-trials")[0]==401
    assert len(api("/api/admin/backup-device-trials",admin=True)[1])==2
    print("PASS expiry, reset-device and delete preserve trial restriction; ledger backup requires admin")
    stop(proc); proc=start()
    assert register("after-restart","third-id",machine("a"))[0]==403
    assert register("after-restart-legacy","device-a",machine("different"))[0]==403
    print("PASS trial history survives server restart and account deletion")
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        results=list(pool.map(lambda i:register(f"race-{i}","race-device",machine("race"),f"192.0.2.{i+1}")[0],range(8)))
    assert results.count(200)==1 and results.count(403)==7,results
    assert register("new-machine","device-new",machine("new"))[0]==200
    status,paid=api("/api/admin/users",dict(email="paid",password="x",days=10),admin=True)
    assert status==200 and paid["active"]
    assert api("/api/auth/login",dict(email="paid",password="x",deviceId="paid-device"))[1]["valid"]
    print("PASS simultaneous registration grants once; separate machine allowed; admin-created account unaffected")
finally:
    stop(proc)
print("All trial API checks passed (local JSON storage; PostgreSQL deployment still requires verification).")
