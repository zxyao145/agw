import { installCryptoGetRandomValues } from "@/polyfills/crypto-install";

describe("crypto random values polyfill", () => {
  test("installs a random source before UUID creation", () => {
    const target: { crypto?: { getRandomValues?: (array: Uint8Array) => Uint8Array } } = {};
    const randomValues = jest.fn((array: Uint8Array) => {
      array.fill(7);
      return array;
    });

    installCryptoGetRandomValues(target, randomValues);
    const bytes = target.crypto!.getRandomValues!(new Uint8Array(4));

    expect(Array.from(bytes)).toEqual([7, 7, 7, 7]);
    expect(randomValues).toHaveBeenCalledTimes(1);
  });

  test("preserves an existing platform implementation", () => {
    const existing = jest.fn((array: Uint8Array) => array);
    const replacement = jest.fn((array: Uint8Array) => array);
    const target = { crypto: { getRandomValues: existing } };

    installCryptoGetRandomValues(target, replacement);

    expect(target.crypto.getRandomValues).toBe(existing);
  });
});
