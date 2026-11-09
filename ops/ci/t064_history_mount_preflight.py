import argparse
import json
import os
import pathlib
import stat
import subprocess
import sys

BOTS=("accabad-prod1-bot-1","accabad-prod2-bot-1","accabad-prod3-bot-1","accabad-staging-bot-1")
STAGE_IMAGE="accabad-panel-app:staging-history-18b2c9e"
NETWORKS={name:name.removesuffix("-bot-1")+"_ai-private" for name in BOTS}

def require(value):
	if not value:
		raise ValueError("Historical archive runtime mount isolation rejected")

def overlaps(first,second):
	a=pathlib.Path(first).resolve()
	b=pathlib.Path(second).resolve()
	return a==b or a in b.parents or b in a.parents

def verify(root,containers):
	require(root.is_absolute() and root.is_dir() and not root.is_symlink())
	info=root.stat()
	require(stat.S_ISDIR(info.st_mode) and info.st_uid==0 and info.st_mode & 0o077==0)
	require(set(containers)==set(BOTS))
	count=0
	for name in BOTS:
		row=containers[name]
		require(row["running"] is True and row["privileged"] is False and row["readonly"] is True)
		require(row["ipc"]=="private" and row["pid"] in ("","private") and row["userns"] in ("","private"))
		require(row["network"]==NETWORKS[name] and row["capadd"] in (None,[]) and row["devices"] in (None,[]))
		if name=="accabad-staging-bot-1":
			require(row["image"]==STAGE_IMAGE)
		mounts=row["mounts"]
		require(isinstance(mounts,list))
		for mount in mounts:
			require(isinstance(mount,dict) and isinstance(mount.get("Source"),str) and isinstance(mount.get("Destination"),str))
			source=mount["Source"]
			destination=mount["Destination"]
			require(source.startswith("/") and destination.startswith("/"))
			require(not overlaps(root,source) and destination!="/var/run/docker.sock" and destination!="/run/docker.sock")
			count+=1
	return {"scope":"t064_owner_private_history_archive_live_bot_mount_isolation","bot_containers":len(containers),"inspected_mounts":count,
		"protected_source_root_exposed_to_bots":False,"staging_synthetic_image_unchanged":True,
		"readonly_root_filesystems":True,"private_namespaces_and_networks":True,"no_added_capabilities_or_devices":True,
		"runtime_qualification_complete":False,"real_customer_history_served":False,"release_ready":False,"matched":True}

def inspect():
	result={}
	for name in BOTS:
		call=subprocess.run(["docker","inspect","--format","{{json .Mounts}}|{{.HostConfig.Privileged}}|{{.State.Running}}|{{.Config.Image}}|{{.HostConfig.ReadonlyRootfs}}|{{.HostConfig.IpcMode}}|{{.HostConfig.PidMode}}|{{.HostConfig.UsernsMode}}|{{.HostConfig.NetworkMode}}|{{json .HostConfig.CapAdd}}|{{json .HostConfig.Devices}}",name],
			text=True,capture_output=True,timeout=12,check=False)
		require(call.returncode==0 and len(call.stdout)<65536 and not call.stderr)
		parts=call.stdout.strip().split("|")
		require(len(parts)==11 and parts[1] in ("true","false") and parts[2] in ("true","false") and parts[4] in ("true","false"))
		result[name]={"mounts":json.loads(parts[0]),"privileged":parts[1]=="true","running":parts[2]=="true","image":parts[3],
			"readonly":parts[4]=="true","ipc":parts[5],"pid":parts[6],"userns":parts[7],"network":parts[8],
			"capadd":json.loads(parts[9]),"devices":json.loads(parts[10])}
	return result

def main():
	parser=argparse.ArgumentParser()
	parser.add_argument("--protected-root",required=True,type=pathlib.Path)
	parser.add_argument("--receipt",required=True,type=pathlib.Path)
	args=parser.parse_args()
	try:
		parent=args.receipt.parent
		require(args.receipt.is_absolute() and not args.receipt.exists() and parent.is_dir() and not parent.is_symlink() and parent.stat().st_mode & 0o077==0)
		result=verify(args.protected_root,inspect())
		fd=os.open(args.receipt,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
		with os.fdopen(fd,"w") as file:
			json.dump(result,file,sort_keys=True,indent=2)
			file.write("\n")
			file.flush()
			os.fsync(file.fileno())
		print("PASS: protected archive source mounts absent from all three production bots and synthetic staging")
		return 0
	except (OSError,ValueError,KeyError,TypeError,subprocess.TimeoutExpired,json.JSONDecodeError):
		print("FAIL: historical archive runtime mount isolation rejected",file=sys.stderr)
		return 1

if __name__=="__main__":
	sys.exit(main())
