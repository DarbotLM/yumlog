import hashlib,json,os,subprocess,sys
from pathlib import Path
if len(sys.argv)<2: sys.exit('usage: make_runbooks.py <review-folder>')
root=Path(sys.argv[1]).resolve()
install=Path(os.environ['LOCALAPPDATA'])/'DarbotLabs/Yumlog'
cli=Path(os.environ.get('YUMLOG_CLI_ROOT') or Path((install/'current.txt').read_text(encoding='utf-8-sig').strip())/'Yumlog.Native.Cli')/'Yumlog.Native.Cli.exe'
reports=[]
for file in sorted(root.glob('*-evidence.mdkg')):
 name=file.stem.removesuffix('-evidence')
 args=[str(cli),'runbook','--input',str(file),'--json-out',str(root/(name+'-yumlog-runbook.json')),'--markdown-out',str(root/(name+'-yumlog-runbook.md'))]
 result=subprocess.run(args,capture_output=True,text=True)
 if result.returncode:raise RuntimeError(result.stderr)
 report=json.loads((root/(name+'-yumlog-runbook.json')).read_text(encoding='utf-8-sig'))
 reports.append({'film':name,'mdkg':file.name,'mdkgSha256':hashlib.sha256(file.read_bytes()).hexdigest(),'runbook':name+'-yumlog-runbook.json','exitCode':result.returncode,'summary':{k:v for k,v in report.items() if k!='steps'}})
 print(name+': '+json.dumps({k:v for k,v in report.items() if k not in ['steps','source']}),flush=True)
(root/'yumlog-mdkg-runbook-index.json').write_text(json.dumps(reports,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
