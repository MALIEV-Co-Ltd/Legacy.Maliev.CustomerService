"""Synthetic receipt controls only; no /proc, Docker or native execution."""
import copy
import capture_customer_default_focus_resources as verifier

receipt = {'githubSha': 'a' * 40, 'runId': '123', 'runAttempt': '1', 'job': 'focused'}
baseline = dict(receipt, stage='initial', containers=[], nativeProcesses=[], unknownProcesses=[], ownedObservationCommands=[{'terminalVerified': True, 'identityObserved': True, 'actualStartTicks': '123', 'actualExecutable': '/usr/bin/docker', 'exitCode': 0}])
assert verifier.initial_receipt_state(baseline, receipt) == (True, True, set())

def check(label, mutate):
    value = copy.deepcopy(baseline)
    mutate(value)
    if verifier.initial_receipt_state(value, receipt)[1]:
        raise AssertionError('Receipt accepted ' + label)
    print('REFUSED ' + label)

check('wrong stage', lambda x: x.update(stage='terminal'))
check('stale run', lambda x: x.update(runId='other'))
check('stale head', lambda x: x.update(githubSha='b' * 40))
check('stale attempt', lambda x: x.update(runAttempt='2'))
check('foreign job', lambda x: x.update(job='other'))
check('unknown process', lambda x: x.update(unknownProcesses=[{'pid': 1}]))
check('native predecessor', lambda x: x.update(nativeProcesses=[{'pid': 1}]))
check('nonterminal CLI', lambda x: x['ownedObservationCommands'][0].update(terminalVerified=False))
check('unknown birth', lambda x: x['ownedObservationCommands'][0].update(actualStartTicks=None))
check('unknown executable', lambda x: x['ownedObservationCommands'][0].update(actualExecutable=None))
check('timed out CLI', lambda x: x['ownedObservationCommands'][0].update(timedOut=True))
check('overflow output', lambda x: x['ownedObservationCommands'][0].update(outputRefused=True))
check('unobserved containers', lambda x: x.update(containers=None))
check('malformed containers', lambda x: x.update(containers=[{'id': 'invalid'}]))
check('duplicate containers', lambda x: x.update(containers=[{'id': 'c' * 64}, {'id': 'c' * 64}]))
assert verifier.initial_receipt_state(None, receipt) == (False, False, None)
print('17 resource receipt controls passed; no native execution')
