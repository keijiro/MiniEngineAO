MiniEngineAO
------------

![screenshot](http://i.imgur.com/uOYU4YHl.png)

**MiniEngineAO** is a SSAO shader for Unity that was originally developed by
Team Minigraph at Microsoft for their [MiniEngine] library.

[MiniEngine]: https://github.com/Microsoft/DirectX-Graphics-Samples

The MiniEngine SSAO shader has some great points compared to other SSAO
implementations:

- Well optimized for GPU compute. It uses some neat tricks of texture sampling
  and utilizes thread group shared memory to eliminate major bottlenecks in the
  shader so that it keeps throughput high.
- Horizon based method. It provides smoother results compared to other
  Monte Carlo-ish methods.
- Hierarchical approach of denoising and sample distribution. It can
  efficiently eliminate noise and artifacts.

One of the major disadvantages of this method is that it requires GPU compute.
It can't support old graphic APIs (like OpenGL on macOS), and probably runs
slower on mobile GPUs due to limitations in compute units.

This port is still under development and not ready to use in production.
