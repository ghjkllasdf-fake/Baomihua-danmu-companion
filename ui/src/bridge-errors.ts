export class HostCommandError extends Error {
  readonly code: string;
  constructor(message: string, code = 'command-failed') {
    super(message); this.name = 'HostCommandError'; this.code = code;
  }
}
export class HostTimeoutError extends HostCommandError {
  constructor() { super('请求超时，结果尚未确认。', 'timeout'); this.name = 'HostTimeoutError'; }
}
