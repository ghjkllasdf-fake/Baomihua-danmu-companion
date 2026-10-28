export function createNativeNoticeHandler(setError: (update: (current: string) => string) => void) {
  let previous = '';
  return (value: unknown) => {
    if (typeof value !== 'string' || value === previous) return;
    const old = previous; previous = value;
    setError(current => value || (current === old ? '' : current));
  };
}
