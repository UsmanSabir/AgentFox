import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isOrderActionsKey, persistentActions, validateAttentionResolution } from '../src/persistentOrderUi.ts';

const order = (overrides = {}) => ({
  intentId:'intent-1',symbol:'MARI',action:'BUY',quantity:10,orderType:'LIMIT',price:640,
  limitPrice:null,expiresUtc:'2026-09-10T10:00:00Z',state:'working',filledQuantity:0,
  remainingQuantity:10,lastAttemptSessionDate:'2026-09-02',attemptCount:1,lastOrderNo:null,
  sourceArmedId:null,stateReason:null,note:null,createdUtc:'2026-09-03T10:00:00Z',
  updatedUtc:'2026-09-03T10:00:00Z',terminalUtc:null,canRetry:false,retryReason:'',placements:[],...overrides
});

test('persistent actions preserve supported recovery boundaries', () => {
  assert.deepEqual(persistentActions(order()).map(action => action.id),['inspect','cancel']);
  assert.deepEqual(persistentActions(order({state:'attention'})).map(action => action.id),['inspect','resolve','cancel']);
  assert.deepEqual(persistentActions(order({state:'failed',canRetry:true,retryReason:'Safe after check'})).map(action => action.id),['inspect','retry','cancel']);
  assert.deepEqual(persistentActions(order({state:'cancelled'})).map(action => action.id),['inspect']);
  assert.deepEqual(persistentActions(order({state:'fulfilled'})).map(action => action.id),[]);
});

test('only non-repeated row action keys open the action sheet', () => {
  const event = (overrides = {}) => ({key:'F10',shiftKey:true,ctrlKey:false,metaKey:false,altKey:false,repeat:false,isComposing:false,...overrides});
  assert.equal(isOrderActionsKey(event()),true);
  assert.equal(isOrderActionsKey(event({key:'ContextMenu',shiftKey:false})),true);
  for (const overrides of [{shiftKey:false},{ctrlKey:true},{altKey:true},{metaKey:true},{repeat:true},{isComposing:true},{key:'Enter'}])
    assert.equal(isOrderActionsKey(event(overrides)),false);
});

test('attention resolution requires an exact result, bounded whole fill, and evidence', () => {
  assert.equal(validateAttentionResolution('', '', 'statement', 10).error,'Choose what the broker record shows.');
  for (const quantity of ['',0,1.5,10,11]) assert.equal(validateAttentionResolution('partial',quantity,'statement',10).value,null);
  assert.equal(validateAttentionResolution('filled','', '  ',10).error,'Describe the broker record you checked.');
  assert.deepEqual(validateAttentionResolution('partial','4','  broker statement  ',10).value,
    {resolution:'partial',filledQuantity:4,note:'broker statement'});
  assert.deepEqual(validateAttentionResolution('not_filled','ignored','order book',10).value,
    {resolution:'not_filled',filledQuantity:null,note:'order book'});
});
