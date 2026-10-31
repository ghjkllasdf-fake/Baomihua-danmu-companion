export function installerHelperTrust(files) {
  const protectedFiles = files.filter(file => !file.path.includes('/') || file.path.toLowerCase().endsWith('.dll'));
  return 'procedure AddTrustedHelperFiles(Names, Sizes, Hashes: TStringList);\nbegin\n' + protectedFiles.map(file => {
    if (!file.path || /[\x00-\x1f\\:<>"|?*]/.test(file.path) || file.path.split('/').some(part => !part || part === '.' || part === '..') ||
      !Number.isSafeInteger(file.length) || file.length < 0 || !/^[a-fA-F0-9]{64}$/.test(file.sha256))
      throw new Error('Invalid compiled helper trust record.');
    return `  Names.Add('${file.path.replaceAll('/', '\\').replaceAll("'", "''")}'); Sizes.Add('${file.length}'); Hashes.Add('${file.sha256.toLowerCase()}');`;
  }).join('\n') + '\nend;\n';
}
