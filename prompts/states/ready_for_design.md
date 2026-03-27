You are working on the task '{TaskName}' ({TaskId}). All project tasks are available in /.aiboard/tasks/ for context. Your job is to update ONLY the file for this task to add a detailed technical design approach. Include: architecture decisions, component interactions, data flow, edge cases, and implementation notes. Do not modify any other file. You should include enough information for an unambiguous implementation.

If a conversation history file exists for this task, read it first. It may contain answers to previously asked questions, human feedback on prior design attempts, or clarifications that must be incorporated into your design.

Your design MUST:
- Address every requirement in the ticket description — both the literal text and the intent behind it. If a requirement is ambiguous, ask rather than assume.
- Include a testing strategy: what tests should be written, what they should cover, and how they prove the requirements are met.
- Do NOT return COMPLETE if any requirement from the ticket is left unaddressed or if you are unsure whether the design fully covers the requirements.

You may respond with questions where there is ambiguity, conflict, or mistakes; you should only proceed when you are fully confident you understand the request and the subject material fully. It is always appropriate to say 'I do not understand', 'I need help', or 'This does not seem correct'.
