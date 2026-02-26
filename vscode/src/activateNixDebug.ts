/*---------------------------------------------------------
 * Copyright (C) Microsoft Corporation. All rights reserved.
 *--------------------------------------------------------*/
/*
 * activateNixDebug.ts contains the shared extension code that can be executed both in node.js and the browser.
 */

'use strict';

import * as vscode from 'vscode';
import { WorkspaceFolder, DebugConfiguration, ProviderResult, CancellationToken } from 'vscode';
import { nixLog } from './extension';

export function activateNixDebug(context: vscode.ExtensionContext, factory: vscode.DebugAdapterDescriptorFactory & { dispose(): any; }) {
	nixLog.append(`activateNixDebug(${factory})... `);

	context.subscriptions.push(
		vscode.commands.registerCommand('extension.nix-debug.runEditorContents', (resource: vscode.Uri) => {
			let targetResource = resource;
			if (!targetResource && vscode.window.activeTextEditor) {
				targetResource = vscode.window.activeTextEditor.document.uri;
			}
			if (targetResource) {
				let workspaceFolder =
					vscode.workspace.getWorkspaceFolder(targetResource);
				nixLog.appendLine(`Running file ${targetResource} in folder ${workspaceFolder}`);
				vscode.debug.startDebugging(workspaceFolder, {
					type: 'nix',
					name: 'Run File',
					request: 'launch',
					program: targetResource.fsPath
				}, {
					// noDebug: true
				}
				);
			}
		}),
		vscode.commands.registerCommand('extension.nix-debug.debugEditorContents', (resource: vscode.Uri) => {
			let targetResource = resource;
			if (!targetResource && vscode.window.activeTextEditor) {
				targetResource = vscode.window.activeTextEditor.document.uri;
			}
			if (targetResource) {
				let workspaceFolder = vscode.workspace.getWorkspaceFolder(targetResource);
				nixLog.appendLine(`Debugging file ${targetResource} in folder ${workspaceFolder}`);
				vscode.debug.startDebugging(workspaceFolder, {
					type: 'nix',
					name: 'Debug File',
					request: 'launch',
					program: targetResource.fsPath
				});
			}
		}),
		// vscode.commands.registerCommand('extension.nix-debug.toggleFormatting', (variable) => {
		// 	const ds = vscode.debug.activeDebugSession;
		// 	if (ds) {
		// 		ds.customRequest('toggleFormatting');
		// 	}
		// })
	);

	// context.subscriptions.push(vscode.commands.registerCommand('extension.nix-debug.getProgramName', config => {
	// 	return vscode.window.showInputBox({
	// 		placeHolder: "Please enter the name of a markdown file in the workspace folder",
	// 		value: "readme.md"
	// 	});
	// }));

	// register a configuration provider for 'nix' debug type
	const provider = new NixConfigurationProvider();
	context.subscriptions.push(vscode.debug.registerDebugConfigurationProvider('nix', provider));

	// register a dynamic configuration provider for 'nix' debug type
	context.subscriptions.push(vscode.debug.registerDebugConfigurationProvider('nix', {
		provideDebugConfigurations(folder: WorkspaceFolder | undefined): ProviderResult<DebugConfiguration[]> {
			return [
				{
					name: "Dynamic Launch",
					request: "launch",
					type: "nix",
					program: "${file}"
				},
				{
					name: "Another Dynamic Launch",
					request: "launch",
					type: "nix",
					program: "${file}"
				},
				{
					name: "Nix Launch",
					request: "launch",
					type: "nix",
					program: "${file}"
				}
			];
		}
	}, vscode.DebugConfigurationProviderTriggerKind.Dynamic));

	context.subscriptions.push(vscode.debug.registerDebugAdapterDescriptorFactory('nix', factory));
	context.subscriptions.push(factory);

	// override VS Code's default implementation of the debug hover
	// here we match only Nix "variables", that are words starting with an '$'
	context.subscriptions.push(vscode.languages.registerEvaluatableExpressionProvider('nix', {
		provideEvaluatableExpression(document: vscode.TextDocument, position: vscode.Position): vscode.ProviderResult<vscode.EvaluatableExpression> {

			const VARIABLE_REGEXP = /\$[a-z][a-z0-9]*/ig;
			const line = document.lineAt(position.line).text;

			let m: RegExpExecArray | null;
			while (m = VARIABLE_REGEXP.exec(line)) {
				const varRange = new vscode.Range(position.line, m.index, position.line, m.index + m[0].length);

				if (varRange.contains(position)) {
					return new vscode.EvaluatableExpression(varRange);
				}
			}
			return undefined;
		}
	}));

	// override VS Code's default implementation of the "inline values" feature"
	context.subscriptions.push(vscode.languages.registerInlineValuesProvider('nix', {

		provideInlineValues(document: vscode.TextDocument, viewport: vscode.Range, context: vscode.InlineValueContext) : vscode.ProviderResult<vscode.InlineValue[]> {

			const allValues: vscode.InlineValue[] = [];

			for (let l = viewport.start.line; l <= context.stoppedLocation.end.line; l++) {
				const line = document.lineAt(l);
				var regExp = /\$([a-z][a-z0-9]*)/ig;	// variables are words starting with '$'
				do {
					var m = regExp.exec(line.text);
					if (m) {
						const varName = m[1];
						const varRange = new vscode.Range(l, m.index, l, m.index + varName.length);

						// some literal text
						//allValues.push(new vscode.InlineValueText(varRange, `${varName}: ${viewport.start.line}`));

						// value found via variable lookup
						allValues.push(new vscode.InlineValueVariableLookup(varRange, varName, false));

						// value determined via expression evaluation
						//allValues.push(new vscode.InlineValueEvaluatableExpression(varRange, varName));
					}
				} while (m);
			}

			return allValues;
		}
	}));

	nixLog.appendLine("done");
}

class NixConfigurationProvider implements vscode.DebugConfigurationProvider {

	/**
	 * Massage a debug configuration just before a debug session is being launched,
	 * e.g. add all missing attributes to the debug configuration.
	 */
	resolveDebugConfiguration(folder: WorkspaceFolder | undefined, config: DebugConfiguration, token?: CancellationToken): ProviderResult<DebugConfiguration> {

		// if launch.json is missing or empty
		if (!config.type && !config.request && !config.name) {
			const editor = vscode.window.activeTextEditor;
			if (editor && editor.document.languageId === 'nix') {
				config.type = 'nix';
				config.name = 'Launch';
				config.request = 'launch';
				config.program = '${file}';
				config.nixFlags = [];
			}
		}

		if (!config.program) {
			return vscode.window.showInformationMessage("Cannot find a program to debug").then(_ => {
				nixLog.appendLine("ERR: aborting launch because program wasn't set");
				return undefined;	// abort launch
			});
		}

		nixLog.appendLine("Launching with config: ");
		nixLog.appendLine(`${JSON.stringify(config)}`);
		return config;
	}
}

function pathToUri(path: string) {
	try {
		return vscode.Uri.file(path);
	} catch (e) {
		return vscode.Uri.parse(path);
	}
}