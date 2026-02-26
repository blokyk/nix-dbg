/*---------------------------------------------------------
 * Copyright (C) Microsoft Corporation. All rights reserved.
 *--------------------------------------------------------*/
/*
 * extension.ts (and activateNixDebug.ts) forms the "plugin" that plugs into VS Code and contains the code that
 * connects VS Code with the debug adapter.
 * 
 * extension.ts contains code for launching the debug adapter in three different ways:
 * - as an external program communicating with VS Code via stdin/stdout,
 * - as a server process communicating with VS Code via sockets or named pipes, or
 * - as inlined code running in the extension itself (default).
 * 
 * Since the code in extension.ts uses node.js APIs it cannot run in the browser.
 */

'use strict';

import * as vscode from 'vscode';
import { activateNixDebug } from './activateNixDebug';
import { ProtocolServer } from '@vscode/debugadapter/lib/protocol';
import { spawn } from 'child_process';

export let nixLog: vscode.OutputChannel;

export function activate(context: vscode.ExtensionContext) {
	nixLog = vscode.window.createOutputChannel("Nix Debugger");
	nixLog.appendLine(`activate()`);

	// run the debug adapter as a separate process
	activateNixDebug(context, new DebugAdapterExecutableFactory());
}

export function deactivate() {
	// nothing to do
}

class DebugAdapterExecutableFactory implements vscode.DebugAdapterDescriptorFactory {

	createDebugAdapterDescriptor(_session: vscode.DebugSession, executable: vscode.DebugAdapterExecutable | undefined): vscode.ProviderResult<vscode.DebugAdapterDescriptor> {
		const command = "/home/blokyk/dev/lab/nix-dbg/nix-repl-adapter/bin/Debug/net10.0/nix-dbg";
		// const command = "/home/blokyk/dev/lab/dap/dotnet/src/sample/SampleDebugAdapter/bin/Debug/net10.0/SampleDebugAdapter";
		const args = [];

		return executable = new vscode.DebugAdapterExecutable(command, args, {});

		// const child = spawn(command, args, {});
		// child.once('error', (code, _) => {
		// 	nixLog.appendLine(`Child process exited with error ${code}`);
		// 	server.stop();
		// });
		// nixLog.appendLine(
		// 	`debug adapter executable is running '${command}' with ${args.length} args ${args}`
		// );

		// const server = new ProtocolServer();
		// server.start(child.stdout, child.stdin);
		// child.stderr.on('data', data => nixLog.appendLine(`stderr: ${data}`));
		// // child.stdout.on('data', data => nixLog.appendLine(`stdout: ${data}`));

		// return new vscode.DebugAdapterInlineImplementation(server);
	}

	dispose() {}
}
