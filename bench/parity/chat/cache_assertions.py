"""Compare cache observations without accepting a stale result after a message touch."""
import argparse
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--capture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    capture = json.loads(args.capture.read_text(encoding='utf-8'))
    results = capture['results']
    checks = []
    def add(app, name, expected, actual):
        checks.append({'app': app, 'name': name, 'expected': expected, 'actual': actual, 'passed': expected == actual})
    def shape(value):
        return {key: value[key] for key in ['old_body', 'new_body', 'old_creator', 'new_creator']}
    for app in ['dotnet', 'aot']:
        for stage, expected in results['rails']['observations'].items():
            add(app, stage, shape(expected), shape(results[app]['observations'][stage]))
        for state in ['message_version_unchanged', 'rich_text_version_unchanged', 'noop_changed_message_version', 'noop_indexed', 'noop_non_index_state_unchanged', 'noop_fields_state_unchanged', 'touch_changed_message_version', 'real_edit_body_touched', 'real_edit_room_touched', 'real_edit_indexed', 'real_edit_unread_unchanged', 'client_changed_message_version', 'client_changed_body_version']:
            add(app, state, results['rails'][state], results[app][state])
    original = results['rails']['observations']
    add('rails', 'creator rename stays stale before touch', {'old_creator': True, 'new_creator': False}, {key: original['cache-after-creator-rename'][key] for key in ['old_creator', 'new_creator']})
    add('rails', 'unversioned body edit stays stale before touch', {'old_body': True, 'new_body': False}, {key: original['cache-after-same-version-body-edit'][key] for key in ['old_body', 'new_body']})
    add('rails', 'no-op body assignment preserves stale fragment', shape(original['cache-after-same-version-body-edit']), shape(original['cache-after-no-op-update']))
    add('rails', 'no-op does not touch either version', {'message': False, 'body': False}, {'message': results['rails']['noop_changed_message_version'], 'body': results['rails']['noop_changed_body_version']})
    add('rails', 'no-op keeps versions/cache/unread but still updates index', {'non_index_state': True, 'index_updated': True, 'client_and_detach': True}, {'non_index_state': results['rails']['noop_non_index_state_unchanged'], 'index_updated': results['rails']['noop_indexed'], 'client_and_detach': results['rails']['noop_fields_state_unchanged']})
    add('rails', 'real edit touches/indexes without receiving unread again', {'body': True, 'room': True, 'index': True, 'unread': True}, {'body': results['rails']['real_edit_body_touched'], 'room': results['rails']['real_edit_room_touched'], 'index': results['rails']['real_edit_indexed'], 'unread': results['rails']['real_edit_unread_unchanged']})
    add('rails', 'client change touches message without writing unchanged body', {'message': True, 'body': False}, {'message': results['rails']['client_changed_message_version'], 'body': results['rails']['client_changed_body_version']})
    add('rails', 'touch refreshes creator and body', {'old_body': False, 'new_body': True, 'old_creator': False, 'new_creator': True}, shape(original['cache-after-message-touch']))
    add('rails', 'production cache enabled and operational', {'perform_caching': True, 'cache_class': 'ActiveSupport::Cache::RedisCacheStore', 'cache_roundtrip': 123}, capture['rails_cache_configuration'])
    result = {'assertions': len(checks), 'passed': sum(check['passed'] for check in checks), 'failed': sum(not check['passed'] for check in checks), 'checks': checks}
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['assertions', 'passed', 'failed']}))
    raise SystemExit(bool(result['failed']))


if __name__ == '__main__':
    main()
