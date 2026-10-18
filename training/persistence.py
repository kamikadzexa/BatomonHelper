"""Single output writer and recoverable telemetry on Windows and POSIX."""
from contextlib import contextmanager
from pathlib import Path
import json
import os
import time
import uuid

@contextmanager
def output_writer(directory):
    directory=Path(directory);directory.mkdir(parents=True,exist_ok=True)
    stream=(directory/'.writer.lock').open('a+b')
    if stream.tell()==0:stream.write(b'0');stream.flush()
    stream.seek(0)
    try:
        if os.name=='nt':
            import msvcrt
            msvcrt.locking(stream.fileno(),msvcrt.LK_NBLCK,1)
        else:
            import fcntl
            fcntl.flock(stream,fcntl.LOCK_EX|fcntl.LOCK_NB)
    except OSError as error:
        stream.close();raise RuntimeError('Another process owns this output directory.') from error
    try:yield
    finally:
        stream.seek(0)
        if os.name=='nt':msvcrt.locking(stream.fileno(),msvcrt.LK_UNLCK,1)
        else:fcntl.flock(stream,fcntl.LOCK_UN)
        stream.close()

def append_metrics(directory,row):
    directory=Path(directory);pending=directory/'metrics-pending';path=directory/'metrics.jsonl'
    line=json.dumps(row)+'\n'
    for attempt in range(6):
        try:
            backlog=sorted(pending.glob('*.json')) if pending.exists() else []
            with path.open('a',encoding='utf-8') as stream:
                for entry in backlog:stream.write(entry.read_text(encoding='utf-8').rstrip()+'\n')
                stream.write(line)
            for entry in backlog:entry.unlink()
            return True
        except PermissionError:time.sleep(.02*(attempt+1))
    pending.mkdir(exist_ok=True)
    (pending/(str(time.time_ns())+'-'+uuid.uuid4().hex+'.json')).write_text(line,encoding='utf-8')
    return False
