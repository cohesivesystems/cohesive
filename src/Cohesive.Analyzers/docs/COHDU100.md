# COHDU100: Switch over union uses catch-all arm

Catch-all switch arms (`_`/`default`) can hide newly added union cases.  
Prefer explicit case patterns or `Match(...)` callbacks.

