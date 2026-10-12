"""Private local password entry; ephemeral authenticated SSH test session.

The password goes only to the paired SSH channel's sudo stdin. It is never
written to disk or emitted to stdout/stderr. Request/reply files contain no
credentials. Closing/expiry ends the privileged session.
"""
import argparse,json,pathlib,queue,subprocess,threading,time,tkinter as tk
from tkinter import ttk
parser=argparse.ArgumentParser();parser.add_argument('session',type=pathlib.Path);a=parser.parse_args()
a.session.mkdir(parents=True,exist_ok=True)
status=a.session/'status.json'
def state(value):status.write_text(json.dumps(value))
state({'state':'awaiting_private_password'})
window=tk.Tk();window.title('Steam Frame administrator authentication');window.resizable(False,False)
box=ttk.Frame(window,padding=22);box.pack()
ttk.Label(box,text='Steam Frame · 10.35.78.1',font=('Segoe UI',14,'bold')).pack(anchor='w')
ttk.Label(box,text='Enter the steamos user password to run the AV1 decoder tests.\nIt stays out of chat and is not saved.',padding=(0,12,0,10)).pack(anchor='w')
password=tk.StringVar();entry=ttk.Entry(box,textvariable=password,show='●',width=42);entry.pack(fill='x');entry.focus_set()
label=ttk.Label(box,text='');label.pack(anchor='w',pady=10)
messages=queue.Queue();process=None
def authenticate(secret):
 global process
 options=['-i',r'C:\Users\ellio\AppData\Local\steamos-devkit\devkit_rsa','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',r'UserKnownHostsFile=C:\Users\ellio\AppData\Local\SteamLinkVRSettings\ssh-known-hosts','-o','ConnectTimeout=3']
 try:
  process=subprocess.Popen([r'C:\Windows\System32\OpenSSH\ssh.exe',*options,'steamos@10.35.78.1',"sudo -k -S -p '' /usr/bin/python3 /tmp/steam-frame-av1-lab-20261011/root_session.py"],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
  output=queue.Queue()
  def reader():
   for line in process.stdout:output.put(line)
   output.put(None)
  threading.Thread(target=reader,daemon=True).start()
  process.stdin.write(secret+'\n');process.stdin.flush();secret=None
  line=output.get(timeout=15)
  if not line or not json.loads(line).get('root_ready'):raise RuntimeError('Authentication unavailable')
  messages.put('ready');state({'state':'authenticated','uid':0,'expires_in_seconds':1800})
  began=time.monotonic()
  while time.monotonic()-began<1800 and process.poll() is None:
   requests=sorted(a.session.glob('request-*.json'))
   if not requests:time.sleep(.15);continue
   for file in requests:
    try:
     request=json.loads(file.read_text());action=request.get('action');file.unlink()
     process.stdin.write(json.dumps({'action':action})+'\n');process.stdin.flush()
     if action=='close':break
     response=output.get(timeout=45)
     if response is None:raise RuntimeError('Frame session ended')
     data=json.loads(response)
     (a.session/file.name.replace('request-','reply-')).write_text(json.dumps(data))
    except Exception:
     (a.session/file.name.replace('request-','reply-')).write_text(json.dumps({'ok':False,'error':'Privileged action failed or timed out'}))
     raise
   if action=='close':break
  process.stdin.close();process.wait(timeout=20)
  state({'state':'closed'})
 except Exception:
  if process and process.poll() is None:process.kill();process.wait()
  state({'state':'authentication_or_session_failed'});messages.put('failed')
def submit():
 secret=password.get();password.set('')
 if not secret or '\n' in secret or '\r' in secret:return
 entry.configure(state='disabled');button.configure(state='disabled');label.configure(text='Verifying administrator access…')
 threading.Thread(target=authenticate,args=(secret,),daemon=False).start();secret=None
def cancel():
 if process and process.poll() is None:
  try:process.stdin.write('{"action":"close"}\n');process.stdin.flush()
  except Exception:process.kill()
 state({'state':'cancelled'});window.destroy()
button=ttk.Button(box,text='Authenticate',command=submit);button.pack(side='left')
ttk.Button(box,text='Cancel',command=cancel).pack(side='right')
window.bind('<Return>',lambda event:submit());window.protocol('WM_DELETE_WINDOW',cancel)
def poll():
 try:
  value=messages.get_nowait()
  if value=='ready':window.withdraw()
  else:label.configure(text='Authentication failed or the session ended. No password was saved.');button.configure(state='disabled')
 except queue.Empty:pass
 window.after(150,poll)
poll();window.mainloop()
