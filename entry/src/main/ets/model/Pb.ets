/**
 * 极简 protobuf 编解码器。
 * 只实现 AudioRelay 用到的 wire type：0(varint) / 1(64bit) / 2(length-delimited) / 5(32bit)。
 * 不依赖任何三方库，避免 ArkTS 对动态特性的限制。
 */
export const WIRE_VARINT: number = 0;
export const WIRE_64BIT: number = 1;
export const WIRE_LEN: number = 2;
export const WIRE_32BIT: number = 5;

export function utf8Encode(input: string): Uint8Array {
  const out: number[] = [];
  for (let i = 0; i < input.length; i++) {
    let code: number = input.charCodeAt(i);
    if (code >= 0xD800 && code <= 0xDBFF && i + 1 < input.length) {
      const next: number = input.charCodeAt(i + 1);
      if (next >= 0xDC00 && next <= 0xDFFF) {
        code = 0x10000 + ((code - 0xD800) << 10) + (next - 0xDC00);
        i++;
      }
    }
    if (code < 0x80) {
      out.push(code);
    } else if (code < 0x800) {
      out.push(0xC0 | (code >> 6));
      out.push(0x80 | (code & 0x3F));
    } else if (code < 0x10000) {
      out.push(0xE0 | (code >> 12));
      out.push(0x80 | ((code >> 6) & 0x3F));
      out.push(0x80 | (code & 0x3F));
    } else {
      out.push(0xF0 | (code >> 18));
      out.push(0x80 | ((code >> 12) & 0x3F));
      out.push(0x80 | ((code >> 6) & 0x3F));
      out.push(0x80 | (code & 0x3F));
    }
  }
  return new Uint8Array(out);
}

export function utf8Decode(bytes: Uint8Array): string {
  let result: string = '';
  let i: number = 0;
  while (i < bytes.length) {
    const b0: number = bytes[i++];
    let code: number = 0;
    if (b0 < 0x80) {
      code = b0;
    } else if ((b0 & 0xE0) === 0xC0) {
      code = ((b0 & 0x1F) << 6) | (bytes[i++] & 0x3F);
    } else if ((b0 & 0xF0) === 0xE0) {
      code = ((b0 & 0x0F) << 12) | ((bytes[i++] & 0x3F) << 6) | (bytes[i++] & 0x3F);
    } else {
      code = ((b0 & 0x07) << 18) | ((bytes[i++] & 0x3F) << 12) | ((bytes[i++] & 0x3F) << 6) |
        (bytes[i++] & 0x3F);
    }
    if (code > 0xFFFF) {
      code -= 0x10000;
      result += String.fromCharCode(0xD800 + (code >> 10));
      result += String.fromCharCode(0xDC00 + (code & 0x3FF));
    } else {
      result += String.fromCharCode(code);
    }
  }
  return result;
}

export class PbWriter {
  private out: number[] = [];

  private writeVarintBytes(value: number): void {
    let v: number = Math.floor(value);
    if (v < 0) {
      // 协议里不会出现负数，做兜底避免死循环
      v = v >>> 0;
    }
    while (v > 0x7F) {
      this.out.push((v & 0x7F) | 0x80);
      v = Math.floor(v / 128);
    }
    this.out.push(v);
  }

  private writeTag(field: number, wire: number): void {
    this.writeVarintBytes((field << 3) | wire);
  }

  writeVarint(field: number, value: number): void {
    this.writeTag(field, WIRE_VARINT);
    this.writeVarintBytes(value);
  }

  writeBool(field: number, value: boolean): void {
    this.writeVarint(field, value ? 1 : 0);
  }

  writeBytes(field: number, value: Uint8Array): void {
    this.writeTag(field, WIRE_LEN);
    this.writeVarintBytes(value.length);
    for (let i = 0; i < value.length; i++) {
      this.out.push(value[i]);
    }
  }

  writeString(field: number, value: string): void {
    this.writeBytes(field, utf8Encode(value));
  }

  writeMessage(field: number, value: Uint8Array): void {
    this.writeBytes(field, value);
  }

  size(): number {
    return this.out.length;
  }

  finish(): Uint8Array {
    return new Uint8Array(this.out);
  }
}

export class PbReader {
  private buf: Uint8Array;
  private pos: number = 0;

  constructor(buf: Uint8Array) {
    this.buf = buf;
  }

  atEnd(): boolean {
    return this.pos >= this.buf.length;
  }

  get position(): number {
    return this.pos;
  }

  readVarint(): number {
    let result: number = 0;
    let shift: number = 0;
    while (this.pos < this.buf.length) {
      const b: number = this.buf[this.pos++];
      result += (b & 0x7F) * Math.pow(2, shift);
      if ((b & 0x80) === 0) {
        break;
      }
      shift += 7;
      if (shift > 49) {
        break;
      }
    }
    return result;
  }

  readTag(): number {
    return this.readVarint();
  }

  readLengthDelimited(): Uint8Array {
    const len: number = this.readVarint();
    const end: number = Math.min(this.pos + len, this.buf.length);
    const slice: Uint8Array = this.buf.subarray(this.pos, end);
    this.pos = end;
    return slice;
  }

  readString(): string {
    return utf8Decode(this.readLengthDelimited());
  }

  skip(wire: number): void {
    if (wire === WIRE_VARINT) {
      this.readVarint();
    } else if (wire === WIRE_LEN) {
      const len: number = this.readVarint();
      this.pos = Math.min(this.pos + len, this.buf.length);
    } else if (wire === WIRE_64BIT) {
      this.pos = Math.min(this.pos + 8, this.buf.length);
    } else if (wire === WIRE_32BIT) {
      this.pos = Math.min(this.pos + 4, this.buf.length);
    } else {
      this.pos = this.buf.length;
    }
  }
}

/** 十六进制转储，便于排查协议帧 */
export function toHex(bytes: Uint8Array, maxLen: number): string {
  const n: number = bytes.length < maxLen ? bytes.length : maxLen;
  let out: string = '';
  for (let i = 0; i < n; i++) {
    const h: string = bytes[i].toString(16);
    out += (h.length < 2 ? '0' + h : h) + ' ';
  }
  if (bytes.length > n) {
    out += `... (${bytes.length} bytes)`;
  }
  return out.trim();
}

/** 追加字节到已有数组，返回新数组（ArkTS 下避免使用 push.apply 之类动态写法） */
export function concatBytes(a: Uint8Array, b: Uint8Array): Uint8Array {
  const out: Uint8Array = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}

/** protobuf delimited 帧：varint 长度前缀 + 消息体 */
export function encodeDelimited(msg: Uint8Array): Uint8Array {
  const prefix: number[] = [];
  let len: number = msg.length;
  while (len > 0x7F) {
    prefix.push((len & 0x7F) | 0x80);
    len = Math.floor(len / 128);
  }
  prefix.push(len);
  const out: Uint8Array = new Uint8Array(prefix.length + msg.length);
  out.set(new Uint8Array(prefix), 0);
  out.set(msg, prefix.length);
  return out;
}

/** 从缓冲区头部解析一个 delimited 帧；不足一帧时返回 undefined */
export function tryParseDelimited(buffer: Uint8Array): DelimitedResult | undefined {
  // 空缓冲区必须直接返回，否则 consumed=0 会让调用方的 while 循环空转（主线程卡死）
  if (buffer.length === 0) {
    return undefined;
  }
  let len: number = 0;
  let shift: number = 0;
  let idx: number = 0;
  while (idx < buffer.length) {
    const b: number = buffer[idx];
    len += (b & 0x7F) * Math.pow(2, shift);
    idx++;
    if ((b & 0x80) === 0) {
      break;
    }
    shift += 7;
    if (shift > 49) {
      return undefined;
    }
  }
  if ((buffer[idx - 1] & 0x80) !== 0) {
    return undefined;
  }
  if (buffer.length < idx + len) {
    return undefined;
  }
  if (len < 0) {
    return undefined;
  }
  const frame: Uint8Array = buffer.subarray(idx, idx + len);
  const consumed: number = idx + len;
  return new DelimitedResult(frame, consumed);
}

export class DelimitedResult {
  frame: Uint8Array;
  consumed: number;

  constructor(frame: Uint8Array, consumed: number) {
    this.frame = frame;
    this.consumed = consumed;
  }
}
