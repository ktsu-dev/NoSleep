## v1.2.0 (minor)

Changes since v1.1.0:

- Go inactive when the platform refuses a replacement inhibitor (#48) (matt-edmondson)
- Skip a non-executable PATH match when looking up a helper [patch] (#35) (matt-edmondson)
- Refuse an Acquire that was queued behind Dispose [patch] (#36) (matt-edmondson)
- Read and replace the request under one lock in SetKeepDisplayAwake [minor] (#42) (matt-edmondson)
- Include an inhibitor helper's stderr when it exits immediately [patch] (#40) (matt-edmondson)
- Add CompatibilitySuppressions.xml for the Polyfill 11.4.5 shim drift [patch] (#38) (matt-edmondson)
- Run the concurrent Toggle test with an odd and an even thread count (Claude)
- Decide and apply a Toggle under one lock, so concurrent toggles each flip once [patch] (Claude)

