# Completion requirements

For migration, release, compatibility, or feature-wiring work, an intermediate reduction in open
items is not a completion condition and must not be handed off as the requested result.

Work continues until every user-requested active module passes its executable release gates with:

- zero unclassified authority methods;
- zero blocked or unwired feature records;
- zero strict gameplay-authority failures;
- passing builds and applicable tests;
- regenerated and verified payload, hashes, and server artifacts; and
- updated migration documentation and changelog.

If no external decision, credential, missing source, or unavailable dependency prevents further
work, continue implementing instead of returning an intermediate status. Never weaken a validator,
relabel an unwired method, or describe a shrinking ratchet as completion.
