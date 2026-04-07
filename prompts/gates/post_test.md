Verify that the QA agent completed the test validation for task '{TaskName}' ({TaskId}).

## Task

{TaskBody}

## QA Report

{AgentReport}

## Checklist

Verify ALL of the following:
1. The QA agent ran tests and verified they pass.
2. All requirements stated in the task were validated.
3. The agent did not skip or defer any required verification.
4. Any test failures or concerns were explicitly reported.
5. Memory bank documentation (`memory-bank/*.md`) was reviewed and updated if the implementation introduced new patterns, components, dependencies, or architectural changes. If no updates were needed, the agent should have confirmed the docs are current.

Do NOT evaluate:
- Whether the tests are well-written
- Test coverage adequacy beyond what the task required
- Code quality or implementation choices
- Whether alternative testing approaches would be better
- Whether documentation updates are comprehensive beyond what the implementation changed

If all checklist items pass: return COMPLETE.
If minor concerns need human review: return NEEDS_INFO with the concerns in the questions array.
If requirements were clearly not validated: return ERROR describing what was missed.
