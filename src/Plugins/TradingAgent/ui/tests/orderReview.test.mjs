import test from 'node:test';
import assert from 'node:assert/strict';
import { uncertainOrderFailure, isOrderReviewKey } from '../src/orderReview.ts';

test('transport failures and unknown responses never mean safe to retry', () => {
  for (const [status,code] of [[null,null],[500,null],[503,'failed'],[408,null],[409,'outcome_unknown']]) {
    assert.equal(uncertainOrderFailure(status,code),true);
  }
  for (const status of [400,401,403,422,429]) assert.equal(uncertainOrderFailure(status,'refused'),false);
});
test('review shortcut ignores repeated keys, composition and unrelated modifiers', () => {
  const key = {key:'Enter',ctrlKey:true,metaKey:false,altKey:false,shiftKey:false,repeat:false,isComposing:false};
  assert.equal(isOrderReviewKey(key),true);
  assert.equal(isOrderReviewKey({...key,ctrlKey:false,metaKey:true}),true);
  for (const flag of ['repeat','isComposing','altKey','shiftKey']) assert.equal(isOrderReviewKey({...key,[flag]:true}),false);
  assert.equal(isOrderReviewKey({...key,ctrlKey:false}),false);
});
