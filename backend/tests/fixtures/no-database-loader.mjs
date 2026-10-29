import { registerHooks } from 'node:module';
const forbidden = /(?:^|\/)(?:@electric-sql\/(?:pglite|pglite-tools)|drizzle-orm|esbuild|@esbuild\/[^/]+)(?:\/|$)/;
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (forbidden.test(specifier)) throw new Error('Forbidden runtime dependency: ' + specifier);
    const result = nextResolve(specifier, context);
    if (forbidden.test(result.url)) throw new Error('Forbidden runtime dependency: ' + result.url);
    return result;
  }
});
