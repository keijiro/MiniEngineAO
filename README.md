MiniEngineAO
------------

![screenshot](http://i.imgur.com/t5rHYXel.png)

**MiniEngineAO** is a SSAO shader for Unity that was originally developed by
Team Minigraph at Microsoft for their [MiniEngine] library.

[MiniEngine]: https://github.com/Microsoft/DirectX-Graphics-Samples

MiniEngine SSAO has some great points compared to other SSAO implementations.

- Well optimized for GPU compute. It utilizes thread group shared memory to
  eliminate major bottlenecks in the shader so that it keeps throughput high.
- Horizon based method. It provides smoother results compared to other
  Monte Carlo-ish approaches.
- Hierarchical approach of denoising and sample distribution.

One of the major disadvantages of this method is that it requires GPU compute.
It doesn't support old platforms (like OpenGL ES 2.0), and probably runs slow
on mobile GPUs due to differences in compute units compared to desktop ones.

This port is still under development and not ready to use in production.
