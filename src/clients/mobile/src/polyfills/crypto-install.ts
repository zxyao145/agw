export type RandomValuesFunction = (array: Uint8Array) => Uint8Array;

type CryptoLike = {
  getRandomValues?: RandomValuesFunction;
};

export type CryptoGlobal = {
  crypto?: CryptoLike;
};

export function installCryptoGetRandomValues(
  target: CryptoGlobal,
  getRandomValues: RandomValuesFunction,
): void {
  const crypto = target.crypto ?? {};

  if (!target.crypto) {
    Object.defineProperty(target, "crypto", {
      configurable: true,
      value: crypto,
      writable: true,
    });
  }

  if (typeof crypto.getRandomValues !== "function") {
    Object.defineProperty(crypto, "getRandomValues", {
      configurable: true,
      value: getRandomValues,
      writable: true,
    });
  }
}
