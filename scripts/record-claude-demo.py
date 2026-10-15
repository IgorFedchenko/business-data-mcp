#!/usr/bin/env python3
"""Capture only an actual Claude conversation with our synthetic read-only MCP tools.

This is opt-in, never run by CI. Requires existing first-party OAuth login; API-key
billing is refused. Nothing from account metadata, system prompts or raw session
initialization is written to disk. Render with render-demo.py after review.
"""
import datetime
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
auth = json.loads(subprocess.check_output(['claude', 'auth', 'status'], text=True))
if not auth.get('loggedIn') or auth.get('authMethod') != 'oauth_token' or auth.get('apiProvider') != 'firstParty':
    raise SystemExit('Use an existing first-party Claude OAuth subscription login. API-key billing is not enabled by this script.')
prompt = ('Use the business-data MCP server to look up order ORD-1001, then get that customer\'s order history. '
          'Answer with the customer name, the order status and total, and the other order\'s ID, status and total. '
          'Use only the MCP tools; make no writes. Keep the answer under 100 words.')
with tempfile.TemporaryDirectory(prefix='business-mcp-claude-') as fixture:
    database = Path(fixture) / 'demo.db'
    config = Path(fixture) / 'mcp.json'
    config.write_text(json.dumps({'mcpServers': {'business-data': {
        'command': shutil.which('dotnet'),
        'args': [str(root / 'src/BusinessData.Server/bin/Release/net8.0/BusinessData.Server.dll'), '--transport', 'stdio'],
        'env': {'MCP_DATABASE': str(database), 'MCP_ALLOW_WRITES': 'false'}
    }}}))
    env = dict(os.environ)
    for key in ('ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_BASE_URL', 'CLAUDECODE'):
        env.pop(key, None)
    command = ['claude', '-p', prompt, '--strict-mcp-config', str(config), '--setting-sources', '',
               '--tools', '', '--allowedTools', 'mcp__business-data__get_order,mcp__business-data__get_customer_history',
               '--permission-mode', 'dontAsk', '--disable-slash-commands', '--no-session-persistence', '--no-chrome',
               '--output-format', 'stream-json', '--verbose',
               '--system-prompt', 'You are demonstrating a synthetic business-data MCP server. Use its read tools, then answer the user. Never use files, shell, web, agents, writes or any other tool.']
    completed = subprocess.run(command, cwd=root, env=env, capture_output=True, text=True, timeout=180)
    if completed.returncode:
        raise SystemExit('Claude capture failed; no raw output or account data was saved. Exit code: ' + str(completed.returncode))
    events = []
    tool_ids = set()
    model = None
    answer = None
    for line in completed.stdout.splitlines():
        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue
        if message.get('type') == 'assistant':
            model = message.get('message', {}).get('model', model)
            for block in message.get('message', {}).get('content', []):
                if block.get('type') == 'tool_use' and block.get('name', '').startswith('mcp__business-data__'):
                    tool_ids.add(block['id'])
                    events.append({'type': 'tool_call', 'name': block['name'], 'arguments': block.get('input', {})})
        if message.get('type') == 'user':
            for block in message.get('message', {}).get('content', []):
                if isinstance(block, dict) and block.get('type') == 'tool_result' and block.get('tool_use_id') in tool_ids:
                    events.append({'type': 'tool_result', 'content': block.get('content', '')})
        if message.get('type') == 'result' and not message.get('is_error'):
            answer = message.get('result')
    if not answer or len(tool_ids) < 2:
        raise SystemExit('Capture did not contain both actual read tool calls and a final answer; no demo was saved.')
    with sqlite3.connect(database) as db:
        audit = [dict(zip(('actor', 'tool', 'outcome', 'resultCode'), row)) for row in db.execute(
            'SELECT actor,tool,outcome,result_code FROM audit ORDER BY rowid')]
    assert {'get_order', 'get_customer_history'} <= {a['tool'] for a in audit if a['outcome'] == 'success'}, audit
    assert all(a['actor'] == 'stdio:local' for a in audit), audit
    capture = {'capturedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
               'client': 'Claude Code', 'model': model, 'transport': 'stdio', 'data': 'synthetic',
               'presentation': 'Rendered replay of actual tool calls; delays condensed.',
               'prompt': prompt, 'events': events, 'answer': answer, 'serverAudit': audit}
    (root / 'docs/demo-transcript.json').write_text(json.dumps(capture, indent=2) + '\n')
    print('Captured real Claude tool calls, final answer and sanitized server audit into docs/demo-transcript.json.')
