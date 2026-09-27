import { test } from 'node:test';
import assert from 'node:assert/strict';
import { schemaType } from './generate-contracts.mjs';

test('keeps required base properties when generating oneOf variants', () => {
  const output = schemaType({ type: 'object', required: ['id', 'time'], properties: { id: { type: 'string' }, time: { type: 'string' } }, oneOf: [
    { type: 'object', required: ['kind'], properties: { kind: { const: 'engine' }, actor: { type: 'null' } } },
    { type: 'object', required: ['kind'], properties: { kind: { const: 'human' }, actor: { type: 'string' } } },
  ] });
  assert.match(output, /"id": string/);
  assert.match(output, /"time": string/);
  assert.match(output, / & \(/);
  assert.match(output, /"engine"/);
  assert.match(output, /"human"/);
});

test('distinguishes nullable required properties from optional properties', () => {
  assert.equal(schemaType({ type: 'object', additionalProperties: false, required: ['a'], properties: { a: { type: ['string', 'null'] }, b: { type: 'string' } } }), '{ "a": string | null; "b"?: string; }');
});

test('does not invent numeric enum tokens or date-time types absent in OpenAPI', () => {
  assert.equal(schemaType({ type: 'integer' }), 'number');
  assert.equal(schemaType({ format: 'date-time' }), 'unknown');
  assert.equal(schemaType({ enum: ['Open', 'Resolved'], type: 'string' }), '"Open" | "Resolved"');
  assert.equal(schemaType({}), 'unknown');
  assert.equal(schemaType(false), 'never');
});

test('rejects unsupported structures and unresolved references rather than emitting precise-looking types', () => {
  assert.throws(() => schemaType({ not: { type: 'null' } }), /Unsupported/);
  assert.throws(() => schemaType({ $ref: '#/components/schemas/Missing' }), /Invalid schema reference/);
  assert.throws(() => schemaType({ $ref: 'https://example.test/schema' }), /Unsupported reference/);
  assert.throws(() => schemaType({ const: { a: 1 } }), /Unsupported object/);
});

test('typed additional properties do not make differently typed declared fields impossible', () => {
  assert.equal(schemaType({ type: 'object', required: ['enabled'], properties: { enabled: { type: 'boolean' } }, additionalProperties: { type: 'string' } }), '{ "enabled": boolean; } & Record<string, string | boolean>');
});

test('intersections parenthesize union alternatives and references retain sibling constraints', () => {
  assert.equal(schemaType({ allOf: [{ type: ['string', 'null'] }, { enum: ['Up'] }] }), '(string | null) & ("Up")');
  assert.equal(schemaType({ $ref: '#/components/schemas/IncidentStatus', enum: ['Open'] }), '("Open") & IncidentStatus');
});
