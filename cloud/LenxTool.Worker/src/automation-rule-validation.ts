import {
  CatalogApiError,
  assertOnlyFields,
  requireBoolean,
  requireInteger
} from "./catalog";

export type AutomationMatchMode = "ALL" | "ANY";
export type AutomationField =
  | "FEED"
  | "CATEGORY"
  | "TITLE"
  | "AUTHOR"
  | "CONTENT"
  | "LANGUAGE"
  | "PUBLISHED_AT"
  | "HAS_AUDIO"
  | "HAS_VIDEO";
export type AutomationOperator = "EQUALS" | "CONTAINS" | "REGEX" | "BEFORE" | "AFTER" | "EXISTS";
export type AutomationActionType =
  | "ADD_TAG"
  | "HIDE"
  | "MARK_READ"
  | "GENERATE_SUMMARY"
  | "TRANSLATE"
  | "SEND_TO_MEDIA"
  | "NOTIFY";

export interface AutomationCondition {
  field: AutomationField;
  operator: AutomationOperator;
  value: string | null;
}

export interface AutomationAction {
  type: AutomationActionType;
  order: number;
  value: string | null;
}

export interface AutomationRuleDefinition {
  name: string;
  priority: number;
  conflictOrder: number;
  isEnabled: boolean;
  matchMode: AutomationMatchMode;
  conditions: AutomationCondition[];
  actions: AutomationAction[];
}

export interface AutomationRuleSnapshot extends AutomationRuleDefinition {
  id: string;
  version: number;
}

export const automationRuleLimits = Object.freeze({
  maximumRules: 100,
  maximumConditions: 16,
  maximumActions: 8,
  maximumTextLength: 512,
  maximumRegexLength: 256,
  regexTimeoutMilliseconds: 100
});

const fields = new Set<AutomationField>([
  "FEED",
  "CATEGORY",
  "TITLE",
  "AUTHOR",
  "CONTENT",
  "LANGUAGE",
  "PUBLISHED_AT",
  "HAS_AUDIO",
  "HAS_VIDEO"
]);
const operators = new Set<AutomationOperator>([
  "EQUALS",
  "CONTAINS",
  "REGEX",
  "BEFORE",
  "AFTER",
  "EXISTS"
]);
const actionTypes = new Set<AutomationActionType>([
  "ADD_TAG",
  "HIDE",
  "MARK_READ",
  "GENERATE_SUMMARY",
  "TRANSLATE",
  "SEND_TO_MEDIA",
  "NOTIFY"
]);
const translationLanguages = new Map<string, string>([
  ["zh-hans", "zh-Hans"],
  ["en", "en"],
  ["ja", "ja"],
  ["ko", "ko"]
]);
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;
const timestampPattern =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|([+-])(\d{2}):(\d{2}))$/u;
const controlCharacterPattern = /[\u0000-\u001f\u007f]/u;

export function validateAndNormalizeAutomationRule(value: unknown): AutomationRuleDefinition {
  const input = requireRecord(value, "自动化规则");
  assertOnlyFields(input, [
    "name",
    "priority",
    "conflictOrder",
    "isEnabled",
    "matchMode",
    "conditions",
    "actions"
  ]);

  const matchMode = requireEnum(input.matchMode, new Set<AutomationMatchMode>(["ALL", "ANY"]), "匹配模式");
  const rawConditions = requireArray(
    input.conditions,
    1,
    automationRuleLimits.maximumConditions,
    "规则条件"
  );
  const rawActions = requireArray(
    input.actions,
    1,
    automationRuleLimits.maximumActions,
    "规则动作"
  );
  const actions = rawActions
    .map(normalizeAction)
    .sort((left, right) => left.order - right.order);

  if (new Set(actions.map(action => action.order)).size !== actions.length) {
    throw validationError("规则动作顺序不能重复");
  }
  const singletonTypes = actions
    .filter(action => action.type !== "ADD_TAG")
    .map(action => action.type);
  if (new Set(singletonTypes).size !== singletonTypes.length) {
    throw validationError("除加标签外，同类动作不能重复");
  }

  return {
    name: normalizeText(input.name, 120, "规则名称"),
    priority: requireInteger(input.priority, 0, 1000, "规则优先级"),
    conflictOrder: requireInteger(input.conflictOrder, 0, 1000, "规则冲突顺序"),
    isEnabled: requireBoolean(input.isEnabled, "规则启用状态"),
    matchMode,
    conditions: rawConditions.map(normalizeCondition),
    actions
  };
}

export function canonicalJson(value: unknown): string {
  if (value === null || typeof value !== "object") return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  const record = value as Record<string, unknown>;
  return `{${Object.keys(record)
    .sort()
    .map(key => `${JSON.stringify(key)}:${canonicalJson(record[key])}`)
    .join(",")}}`;
}

function normalizeCondition(value: unknown): AutomationCondition {
  const input = requireRecord(value, "规则条件");
  assertOnlyFields(input, ["field", "operator", "value"]);
  const field = requireEnum(input.field, fields, "规则字段");
  const operator = requireEnum(input.operator, operators, "规则操作符");
  if (!operatorAllowed(field, operator)) {
    throw validationError("规则字段与操作符组合无效");
  }

  if (operator === "EXISTS") {
    if (input.value !== undefined && input.value !== null) {
      throw validationError("exists 操作符不接受值");
    }
    return { field, operator, value: null };
  }
  if (typeof input.value !== "string") {
    throw validationError("规则条件值格式无效");
  }

  let normalized: string;
  switch (field) {
    case "FEED":
    case "CATEGORY":
      normalized = normalizeUuid(input.value);
      break;
    case "PUBLISHED_AT":
      normalized = normalizeTimestamp(input.value);
      break;
    case "HAS_AUDIO":
    case "HAS_VIDEO":
      normalized = normalizeBoolean(input.value);
      break;
    default:
      normalized = operator === "REGEX"
        ? normalizePortableRegex(input.value)
        : normalizeText(input.value, automationRuleLimits.maximumTextLength, "规则条件值");
      break;
  }
  return { field, operator, value: normalized };
}

function normalizeAction(value: unknown): AutomationAction {
  const input = requireRecord(value, "规则动作");
  assertOnlyFields(input, ["type", "order", "value"]);
  const type = requireEnum(input.type, actionTypes, "规则动作类型");
  const order = requireInteger(input.order, 0, 1000, "规则动作顺序");
  let normalizedValue: string | null;
  if (type === "ADD_TAG") {
    normalizedValue = normalizeText(input.value, 80, "标签");
  } else if (type === "TRANSLATE") {
    const language = normalizeText(input.value, 16, "翻译目标语言");
    normalizedValue = translationLanguages.get(language.toLowerCase()) ?? null;
    if (normalizedValue === null) throw validationError("翻译目标语言不受支持");
  } else {
    if (input.value !== undefined && input.value !== null) {
      throw validationError("该规则动作不接受任意载荷");
    }
    normalizedValue = null;
  }
  return { type, order, value: normalizedValue };
}

function operatorAllowed(field: AutomationField, operator: AutomationOperator): boolean {
  switch (field) {
    case "FEED":
      return operator === "EQUALS";
    case "CATEGORY":
      return operator === "EQUALS" || operator === "EXISTS";
    case "TITLE":
    case "AUTHOR":
    case "CONTENT":
      return operator === "EQUALS" || operator === "CONTAINS" ||
        operator === "REGEX" || operator === "EXISTS";
    case "LANGUAGE":
      return operator === "EQUALS" || operator === "EXISTS";
    case "PUBLISHED_AT":
      return operator === "BEFORE" || operator === "AFTER" || operator === "EXISTS";
    case "HAS_AUDIO":
    case "HAS_VIDEO":
      return operator === "EQUALS" || operator === "EXISTS";
  }
}

function normalizePortableRegex(value: string): string {
  if (value.length < 1 ||
      value.length > automationRuleLimits.maximumRegexLength ||
      value.trim().length === 0 ||
      controlCharacterPattern.test(value)) {
    throw validationError("正则表达式长度或字符无效");
  }
  for (let index = 0; index < value.length; index += 1) {
    const character = value[index];
    if (character === "\\") {
      index += 1;
      const escaped = value[index];
      if (escaped === undefined || /[1-9]/u.test(escaped) ||
          (escaped === "k" && value[index + 1] === "<") ||
          (escaped === "u" && value[index + 1] === "{")) {
        throw validationError("正则表达式包含不受支持的回溯结构");
      }
      continue;
    }
    if (character === "(" && value[index + 1] === "?" &&
        value.slice(index, index + 3) !== "(?:") {
      throw validationError("正则表达式包含不受支持的回溯结构");
    }
  }
  try {
    // Compilation only: Worker never evaluates a rule regular expression.
    new RegExp(value, "u");
  } catch {
    throw validationError("正则表达式语法无效");
  }
  return value;
}

function normalizeTimestamp(value: string): string {
  const match = timestampPattern.exec(value.trim());
  if (!match) throw validationError("发布时间必须是带时区的 ISO 8601 时间");
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  const offsetHour = match[10] === undefined ? 0 : Number(match[10]);
  const offsetMinute = match[11] === undefined ? 0 : Number(match[11]);
  if (year < 1 || year > 9999 ||
      month < 1 || month > 12 ||
      day < 1 || day > daysInMonth(year, month) ||
      hour > 23 || minute > 59 || second > 59 ||
      offsetHour > 14 || offsetMinute > 59 ||
      (offsetHour === 14 && offsetMinute !== 0)) {
    throw validationError("发布时间超出有效范围");
  }
  const parsed = new Date(value.trim());
  const utcYear = parsed.getUTCFullYear();
  if (Number.isNaN(parsed.getTime()) || utcYear < 1 || utcYear > 9999) {
    throw validationError("发布时间格式无效");
  }
  return parsed.toISOString();
}

function daysInMonth(year: number, month: number): number {
  if (month === 2) {
    const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
    return leap ? 29 : 28;
  }
  return [4, 6, 9, 11].includes(month) ? 30 : 31;
}

function normalizeUuid(value: string): string {
  const normalized = value.trim();
  if (!uuidPattern.test(normalized)) throw validationError("Feed 或分类 ID 格式无效");
  return normalized.toLowerCase();
}

function normalizeBoolean(value: string): string {
  const normalized = value.trim().toLowerCase();
  if (normalized !== "true" && normalized !== "false") {
    throw validationError("音视频存在状态必须是 true 或 false");
  }
  return normalized;
}

function normalizeText(value: unknown, maximumLength: number, label: string): string {
  if (typeof value !== "string") throw validationError(`${label}格式无效`);
  const normalized = value.trim().normalize("NFKC");
  if (normalized.length < 1 ||
      normalized.length > maximumLength ||
      controlCharacterPattern.test(normalized)) {
    throw validationError(`${label}长度或字符无效`);
  }
  return normalized;
}

function requireRecord(value: unknown, label: string): Record<string, unknown> {
  if (value === null || Array.isArray(value) || typeof value !== "object") {
    throw validationError(`${label}必须是对象`);
  }
  return value as Record<string, unknown>;
}

function requireArray(value: unknown, minimum: number, maximum: number, label: string): unknown[] {
  if (!Array.isArray(value) || value.length < minimum || value.length > maximum) {
    throw validationError(`${label}数量超出范围`);
  }
  return value;
}

function requireEnum<T extends string>(value: unknown, values: Set<T>, label: string): T {
  if (typeof value !== "string" || !values.has(value as T)) {
    throw validationError(`${label}无效`);
  }
  return value as T;
}

function validationError(message: string): CatalogApiError {
  return new CatalogApiError(400, "VALIDATION_ERROR", message);
}
