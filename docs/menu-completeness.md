# Menu completeness — why the photo requirement moved, and where it went

Prompt 6 made ingredients, allergens, portion size, prep minutes and a photo **required on a menu
item**. Prompt 10 made them optional to save and required to go live.

If you are reading this because you found a validation rule that looks loosened and you are about
to tighten it back: please read section 3 first. The rule was not dropped. It was moved, and it is
now enforced in three places instead of one.

---

## 1. Why the fields are required at all

Nearly every question a diner puts to a waiter is static data about a dish. What is in it. How big
is it. How long will it take. Does it have nuts. What does it look like.

The product exists to remove those questions. A menu that cannot answer them is a menu the diner
reads and then waves at a waiter anyway, which is the exact failure the whole feature is built
against. And optional fields do not get filled in — nobody sits down on a Tuesday and back-fills
allergen lists for eighty dishes. If they are nullable and unenforced they stay empty for ever.

**That reasoning is still correct and nothing here weakens it.**

## 2. Why the enforcement point was wrong

Requiring them on `POST` meant an eighty-dish menu could not be entered without eighty photo
uploads first, in order, before a single name or price could be typed.

That is not the order the work happens in. Onboarding a venue looks like this:

- Somebody from the team sits in the cafe with the owner and types the menu — names, prices,
  categories — in one sitting, from the printed card on the table.
- The photographs are taken on a different day, by somebody else, often the following week, and
  frequently after the kitchen has changed two of the dishes.

The old rule made the first of those impossible. The team either could not start, or entered
placeholder text and a stock photo into every field — which is strictly worse than an empty field,
because an empty field is visibly unfinished and `"allergens": "none"` typed to get past a form is
indistinguishable from a real answer.

It is also the same shape of bug as the one Prompt 9 fixed: a required `PhotoUrl` that nothing in
the product could produce a value for, which is why no venue could be onboarded at all.

## 3. Where the rule went

The requirement now lives at the moment it actually matters — the moment the branch starts taking
diners — and in the read that actually faces one.

**One definition.** `MenuItemCompleteness.Rule` in the domain is the only statement of what
complete means: a photo, a description, ingredients, allergens, a portion size and a positive prep
time. Everything below reads that one expression — compiled for in-memory use, negated for the
queries that count. **A client must never carry its own copy.** Two definitions drift, and the way
this one would drift is a dish with no allergen list reaching somebody it is dangerous to.

| Where | What it does |
| --- | --- |
| `MenuItemView.isComplete` | Every read of an item says whether it is fit to show a diner. Computed server-side. |
| `GET /api/branches/{id}/readiness` | The onboarding checklist, with `incompleteMenuItemCount` and the ids. |
| `PATCH /api/platform/branches/{id}` → `Paid` | **Refused** while any item is incomplete, with the count in `context.incompleteMenuItemCount`. |
| `GET /api/branches/{id}/menu` (diner) | Incomplete items are **absent**. Not greyed out — absent. |
| `GET /api/branches/{id}/menu/manage` (console) | Incomplete items are **present**, flagged. |

The last two rows are the pair that matters, and they are deliberately opposite:

- **A manager must see the eleven dishes that still need a photo.** That is the entire point of
  being allowed to save them half-entered.
- **A diner must never see one.** Somebody reading an empty allergen list reasonably concludes
  there are none. A missing photo is cosmetic; a missing allergen list is not.

Note that this is also the opposite treatment from a **sold-out** item, which *is* shown to diners,
flagged with `isAvailable: false`. "We are out of khachapuri tonight" is an answer a diner can act
on. "We have not finished typing this in" is not something to put in front of a customer at all.

**The order endpoint holds the same line.** A diner's phone ordering an item that is not complete -
its id can only have come from somewhere other than the menu - is refused with 409
`menu-item-unavailable` naming the dish, as a sold-out one is, and nothing on that order is placed.
Staff may still key one in from the console, where the unfinished item is visible; that is
deliberate.

## 4. What is still required to save

`name`, `priceAmd` and the category. Something without them is not a partially entered item; it is
not an item.

And optional is not unchecked. A field that *is* supplied is still validated: a prep time of `0` is
a typo rather than "no prep time", an empty guid is not a photo id, and text is trimmed and
length-checked. `Guard.OptionalText` returns `null` for whitespace, which is why the completeness
rule can test for null alone and never has to think about `""` versus `"   "`.

## 5. The go-live gate, precisely

`BranchNotReadyForDinersException` → HTTP 409, code `branch-not-ready`.

It fires on the **transition** to `Paid`, not on being `Paid`. A branch that is already trading and
adds tonight's half-entered special can still be edited; the dish is hidden from diners either way,
which is the protection that actually matters. Gating every edit would make the rule a trap rather
than a checklist.

The gate checks the menu and nothing else. The rest of `readiness` — tablets, staff, hours — is a
checklist rather than a gate, because a venue may legitimately want ordering switched on before it
has finished enrolling tablets, and the platform admin doing the switch can see the list.

## 6. If you are still tempted to move it back

The thing to change is not the create endpoint. If incomplete items are lingering in real venues,
that is a prompt-the-owner problem — the readiness endpoint exists to drive exactly that — and the
answer is a nag in the console, not a form that cannot be filled in on the day somebody is sitting
in the cafe with the owner and the printed menu.
