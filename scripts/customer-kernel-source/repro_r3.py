import importlib.util
from pathlib import Path
from unittest.mock import patch
import signal
import json
path = Path(__file__).parents[1] / 'customer-native-lifecycle-qualification-20261008-r3/owned_process.py'
spec = importlib.util.spec_from_file_location('immutable_r3', path)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)
def fail(_row):
    raise OSError('mock durability fault')
owner = mod.ProcessCustody(lambda _:None, lambda _:False, fail)
with patch.object(mod.signal,'signal'), patch.object(mod.signal,'getsignal'):
    try:
        with owner.signals():
            owner.interrupted = signal.SIGTERM
    except BaseException as error:
        assert type(error) is mod.OwnershipError
        assert owner.interrupted == signal.SIGTERM
        assert any(r['phase']=='durability' for r in owner.failures)
        print(json.dumps({'actualR3Exception':type(error).__name__,'expectedPrimary':'DeferredInterruption','confirmed':True,'secondaryFailures':owner.failures,'allProcessSignalAPIsMocked':True}))
