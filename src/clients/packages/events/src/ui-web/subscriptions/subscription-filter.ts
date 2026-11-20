import type { JsonValue } from "@agw/api";

import { readCommaSeparatedValues } from "../comma-separated-values";
import { readJsonObject, readStringArray } from "../json-values";

export type FilterField = "subject" | "scope" | "tags" | "data";
export type FilterRow = {
  field: FilterField;
  dataPath: string;
  values: string[];
  draftValue?: string;
};

function readSingleProperty(value: unknown, key: string): unknown {
  const record = readJsonObject(value);
  return record && Object.keys(record).length === 1 ? record[key] : undefined;
}

function getFilterRowValues(row: FilterRow): string[] {
  const draftValues = readCommaSeparatedValues(row.draftValue ?? "");
  return draftValues.length > 0 ? [...row.values, ...draftValues] : row.values;
}

export function validateFilterRows(rows: FilterRow[]): string[] {
  const errors: string[] = [];
  rows.forEach((row, index) => {
    const invalidDataPath =
      row.dataPath.length === 0 ||
      row.dataPath.startsWith(".") ||
      row.dataPath.endsWith(".") ||
      row.dataPath.includes("..") ||
      /\s/.test(row.dataPath);
    if (row.field === "data" && invalidDataPath) {
      errors.push(`Condition ${index + 1} needs a valid data path.`);
    }
    const values = getFilterRowValues(row);
    const invalidValues = values.length === 0 || values.some((value) => !value.trim());
    if (invalidValues) {
      errors.push(`Condition ${index + 1} needs at least one value.`);
    }
  });
  return errors;
}

export function filterRowsToJsonLogic(rows: FilterRow[]): JsonValue | null {
  const errors = validateFilterRows(rows);
  if (errors.length > 0) throw new Error(errors[0]);
  if (rows.length === 0) return null;
  return {
    and: rows.map((row) => {
      const values = getFilterRowValues(row);
      if (row.field === "tags") {
        return { some: [{ var: "tags" }, { in: [{ var: "" }, values] }] };
      }
      if (row.field === "data") {
        return { in: [{ cat: [{ var: `data.${row.dataPath}` }] }, values] };
      }
      return { in: [{ var: row.field }, values] };
    }),
  };
}

export function jsonLogicToFilterRows(filter: JsonValue | null | undefined): FilterRow[] | null {
  if (filter === null || filter === undefined) return [];
  const conditions = readSingleProperty(filter, "and");
  if (!Array.isArray(conditions)) return null;
  const rows: FilterRow[] = [];
  for (const condition of conditions) {
    const some = readSingleProperty(condition, "some");
    if (Array.isArray(some) && some.length === 2 && readSingleProperty(some[0], "var") === "tags") {
      const inValues = readSingleProperty(some[1], "in");
      const invalidTags =
        !Array.isArray(inValues) ||
        inValues.length !== 2 ||
        readSingleProperty(inValues[0], "var") !== "";
      if (invalidTags) return null;
      const values = readStringArray(inValues[1]);
      if (!values) return null;
      rows.push({ field: "tags", dataPath: "", values });
      continue;
    }

    const inValues = readSingleProperty(condition, "in");
    if (!Array.isArray(inValues) || inValues.length !== 2) return null;
    const values = readStringArray(inValues[1]);
    if (!values) return null;
    const name = readSingleProperty(inValues[0], "var");
    if (name === "subject" || name === "scope") {
      rows.push({ field: name, dataPath: "", values });
      continue;
    }
    const cat = readSingleProperty(inValues[0], "cat");
    if (!Array.isArray(cat) || cat.length !== 1) return null;
    const path = readSingleProperty(cat[0], "var");
    if (typeof path !== "string" || !path.startsWith("data.")) return null;
    rows.push({ field: "data", dataPath: path.slice(5), values });
  }
  return validateFilterRows(rows).length === 0 ? rows : null;
}
