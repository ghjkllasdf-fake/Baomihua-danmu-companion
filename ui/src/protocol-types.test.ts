import { expect, it } from 'vitest';
import ts from 'typescript';

it('typechecks valid commands and rejects missing context, uncorrelated commands and invented result types without suppressions', () => {
  const root = ts.sys.resolvePath('.').replaceAll('\\', '/');
  const config = ts.readConfigFile(`${root}/tsconfig.json`, ts.sys.readFile);
  const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, root);
  const file = `${root}/src/__protocol_type_probe__.ts`;
  const good = [
    "import { request } from './bridge';",
    "import { defaultSettings } from './model';",
    "request('hello');",
    "request('update-settings', { settings: defaultSettings });",
    "const result: Promise<{count: number}> = request('comments', {apiBase:'http://fixture.invalid/token', value:'1', expectedMediaRevision:0});",
  ];
  const bad = [
    "request('comments', {apiBase:'http://fixture.invalid/token', value:'1'});",
    "request('search');",
    "request('execute', {});",
    "request('update-settings', {settings:{fontSize:40}});",
    "request('hello', {path:'arbitrary'});",
    "const wrong: Promise<string> = request('search', {apiBase:'http://fixture.invalid/token', value:'1'});",
    "const kind = Math.random() > .5 ? 'search' : 'comments'; request(kind, {apiBase:'http://fixture.invalid/token',value:'1'});",
  ];
  const source = [...good, ...bad].join('\n');
  const host = ts.createCompilerHost(parsed.options), read = host.getSourceFile.bind(host);
  host.getSourceFile = (name, languageVersion, onError, shouldCreate) => name === file ? ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true) : read(name, languageVersion, onError, shouldCreate);
  const program = ts.createProgram([file], parsed.options, host);
  const diagnostics = ts.getPreEmitDiagnostics(program);
  expect(diagnostics.filter(d => d.file?.fileName !== file).map(d => ts.flattenDiagnosticMessageText(d.messageText, '\n'))).toEqual([]);
  const lines = diagnostics.map(d => d.file!.getLineAndCharacterOfPosition(d.start!).line);
  expect(new Set(lines)).toEqual(new Set(bad.map((_, index) => good.length + index)));
  expect(lines).toHaveLength(bad.length);
}, 15000);
