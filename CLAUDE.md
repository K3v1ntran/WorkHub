# WorkHub phase workflow

- Start each new implementation phase on its own branch from `main` after the previous phase has been approved and merged.
- At the end of each implementation session, review the diff, commit the in-scope changes, and push the phase branch to GitHub for Codex review. Report the branch, commit, and verification status. Leave unrelated working changes out of the commit.
- During development, push only the phase branch. When the user closes the phase, review the commits, merge the branch into `main`, and push the reviewed `main`. Create the next phase branch from that merged commit. A source push does not create a release.
- Keep pending checks marked pending. Follow the current request for test and release authorization.
- Develop against the tracked .NET 10 solution. The local .NET 8 legacy variant is outside the development and release scope.
