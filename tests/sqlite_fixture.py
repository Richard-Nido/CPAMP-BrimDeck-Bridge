"""Only writes disposable databases inside tests/output. Never opens CPAMP."""
import sqlite3, pathlib, sys, json
path=pathlib.Path(sys.argv[1]).resolve()
if not path.is_relative_to(pathlib.Path(__file__).resolve().parent/'output'):
    raise RuntimeError('Fixture outside tests/output rejected')
path.parent.mkdir(parents=True,exist_ok=True)
c=sqlite3.connect(path, isolation_level=None)
c.execute('pragma journal_mode=WAL')
c.execute('create table if not exists usage_events(timestamp_ms integer, total_tokens integer, failed integer, request_id text, model text)')
c.execute('create index if not exists idx_fixture_time on usage_events(timestamp_ms)')
c.execute('create table if not exists usage_archive_deleted_coverage_daily(min_timestamp_ms integer,max_timestamp_ms integer)')
c.execute('delete from usage_events'); c.execute('delete from usage_archive_deleted_coverage_daily')
print('ready',flush=True)
for line in sys.stdin:
    command=json.loads(line)
    if command['action']=='insert':
        c.execute('insert into usage_events values(?,?,?,?,?)',(command['time'],command['tokens'],command['failed'],command.get('request','duplicate-retry'),command.get('model','model-a')))
    elif command['action']=='clear': c.execute('delete from usage_events'); c.execute('delete from usage_archive_deleted_coverage_daily')
    elif command['action']=='archive': c.execute('insert into usage_archive_deleted_coverage_daily values(0,1000)')
    elif command['action']=='journal_delete': c.execute('pragma journal_mode=DELETE')
    elif command['action']=='lock': c.execute('begin exclusive')
    elif command['action']=='unlock': c.execute('commit')
    elif command['action']=='exit': break
    print('ok',flush=True)
c.close()
