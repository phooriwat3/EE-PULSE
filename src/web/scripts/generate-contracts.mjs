import { readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const source = new URL('../../../docs/api/openapi-v1.json', import.meta.url);
const target = new URL('../src/api/generated.ts', import.meta.url);
const bytes = readFileSync(source);
const document = JSON.parse(bytes);
export function schemaType(schema, names = new Set(Object.keys(document.components.schemas))) {
  if (schema === true) return 'unknown';
  if (schema === false) return 'never';
  for (const keyword of ['not', 'if', 'then', 'else', 'prefixItems', 'dependentSchemas', 'unevaluatedProperties', 'patternProperties']) {
    if (keyword in schema) throw new Error(`Unsupported structural schema keyword: ${keyword}`);
  }
  const { $ref, oneOf, anyOf, allOf, ...base } = schema;
  const parts = [];
  if ($ref) {
    if (!$ref.startsWith('#/components/schemas/')) throw new Error(`Unsupported reference: ${$ref}`);
    const name = $ref.split('/').at(-1);
    if (!names.has(name) || !/^[A-Za-z_$][\w$]*$/.test(name)) throw new Error(`Invalid schema reference: ${$ref}`);
    parts.push(name);
  }
  // JSON Schema applies sibling properties AND composition. In particular,
  // lifecycle oneOf branches must retain the base event IDs and timestamps.
  if (oneOf) parts.push(`(${oneOf.map(value => schemaType(value, names)).join(' | ')})`);
  if (anyOf) parts.push(`(${anyOf.map(value => schemaType(value, names)).join(' | ')})`);
  if (allOf) parts.push(...allOf.map(value => `(${schemaType(value, names)})`));
  if (parts.length) {
    if (base.type || base.properties || base.enum || 'const' in base) parts.unshift(`(${schemaType(base, names)})`);
    return parts.join(' & ');
  }
  function literal(value) {
    if (value !== null && typeof value === 'object') throw new Error('Unsupported object/array literal schema');
    if (typeof value === 'number' && Number.isInteger(value) && !Number.isSafeInteger(value)) throw new Error('Unsafe integer literal schema');
    return JSON.stringify(value);
  }
  if ('const' in schema) return literal(schema.const);
  if (schema.enum) return schema.enum.length ? schema.enum.map(literal).join(' | ') : 'never';
  if (Array.isArray(schema.type)) return schema.type.map(value => schemaType({ ...schema, type: value }, names)).join(' | ');
  switch (schema.type) {
    case 'null': return 'null';
    case 'string': return 'string';
    case 'integer': case 'number': return 'number';
    case 'boolean': return 'boolean';
    case 'array': return `Array<${schemaType(schema.items ?? {}, names)}>`;
    case 'object': {
      const required = new Set(schema.required ?? []);
      const properties = schema.properties ?? {};
      const fields = Object.entries(properties).map(([name, value]) => `${JSON.stringify(name)}${required.has(name) ? '' : '?'}: ${schemaType(value, names)};`);
      for (const name of required) if (!(name in properties)) fields.push(`${JSON.stringify(name)}: unknown;`);
      const object = `{ ${fields.join(' ')} }`;
      if (schema.additionalProperties === false) return object;
      // Index signatures apply to declared keys too; widening the dictionary
      // with field types avoids making valid objects impossible to construct.
      // Exact extra-key constraints and oneOf exclusivity need runtime validation.
      const extras = schemaType(schema.additionalProperties ?? true, names);
      const fieldTypes = Object.values(properties).map(value => schemaType(value, names));
      const indexType = extras === 'unknown' ? 'unknown' : [...new Set([extras, ...fieldTypes, ...(fields.some(field => field.includes('?:')) ? ['undefined'] : [])])].join(' | ');
      return `${object} & Record<string, ${indexType}>`;
    }
    default: return 'unknown';
  }
}
if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const output = `// Generated from docs/api/openapi-v1.json. Do not edit.\n// SHA-256: ${createHash('sha256').update(bytes).digest('hex')}\n// Structural models only: no runtime validation or oneOf exclusivity.\n// Unconstrained schemas remain unknown; formats alone do not imply string.\n// JSON integers are numbers; reject unsafe integer values at application boundaries.\n\n${Object.entries(document.components.schemas).map(([name, schema]) => `export type ${name} = ${schemaType(schema)};`).join('\n')}\n`;
  if (process.argv.includes('--check')) {
    if (readFileSync(target, 'utf8') !== output) throw new Error('Generated contract types are stale. Run npm run generate:contracts.');
  } else writeFileSync(target, output);
}
