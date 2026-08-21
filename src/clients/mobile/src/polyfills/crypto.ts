import { getRandomValues } from "expo-crypto";

import { installCryptoGetRandomValues, type CryptoGlobal } from "./crypto-install";

installCryptoGetRandomValues(globalThis as unknown as CryptoGlobal, getRandomValues);
