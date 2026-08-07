## [1.1.1](https://github.com/lukislp/ObdGarage/compare/v1.1.0...v1.1.1) (2026-08-07)


### Bug Fixes

* add a live dashboard screenshot to the README ([4506202](https://github.com/lukislp/ObdGarage/commit/45062027649a6f9c369e17af104424b94dab650e))

# [1.1.0](https://github.com/lukislp/ObdGarage/compare/v1.0.0...v1.1.0) (2026-08-07)


### Bug Fixes

* add a self-hosted test coverage badge ([e9be5fa](https://github.com/lukislp/ObdGarage/commit/e9be5fa14fddb21a8fca2f531589b8116c9f4764))
* bound fuel cost-per-km to the same window as distance ([91aee87](https://github.com/lukislp/ObdGarage/commit/91aee87aab95b96b5873a67eefcf9dd48a086ad9))
* close socket-leak and stale-IsConnected gaps in Android BT transport ([0168bda](https://github.com/lukislp/ObdGarage/commit/0168bdae9de2d8532c08a2198da3a96668638f68))
* correct stale test counts and descriptions left over from the merge ([fc6ba19](https://github.com/lukislp/ObdGarage/commit/fc6ba19ff9360b735a195f16907115396633a2d4))
* eliminate login timing side-channel for unknown accounts ([58ff6e4](https://github.com/lukislp/ObdGarage/commit/58ff6e42d17254664db3b95cc5795f51b81e6cb2))
* harden OBD protocol parsing and connection handling ([92a4063](https://github.com/lukislp/ObdGarage/commit/92a4063b59448be0654cfb280cc00f3e03b4b3fb))
* harden SyncManager auth persistence and state consistency ([b639ee4](https://github.com/lukislp/ObdGarage/commit/b639ee4c53119e7b5e78dcfd63a870143f749172))
* persist ASP.NET Core Data Protection keys across restarts ([5ce0058](https://github.com/lukislp/ObdGarage/commit/5ce00585885afb24ceec0acb45eabe48f2f9f88b))
* prevent trip-merging across silent connection loss ([a2cf2ae](https://github.com/lukislp/ObdGarage/commit/a2cf2ae1e23ea2a5a29b5be082d24572c9f2ebcf))
* re-trigger CI after the previous push's webhook was dropped during a GitHub Actions incident ([5a8f0ca](https://github.com/lukislp/ObdGarage/commit/5a8f0ca126b9385b57684ca254f54138ad7aa295))
* reject implausible odometer values from OBD reads ([99f45e2](https://github.com/lukislp/ObdGarage/commit/99f45e261035425efcb6ba65a5c038b662da8c90))
* scope AppState/SyncManager per browser session, not process-wide ([9dac81a](https://github.com/lukislp/ObdGarage/commit/9dac81a865e52aa3b9274c68ffc14ca1dd6bffea))
* skip corrupt trailing lines when reading sample history ([71d0e56](https://github.com/lukislp/ObdGarage/commit/71d0e56594ea4e7dc1abb791f86fc13d160bed3b))
* track rejected entity ids in sync push responses ([13c7a2f](https://github.com/lukislp/ObdGarage/commit/13c7a2fd4a8a87f79b35a2ea8fd59d1fad80c509))
* web UI formatting and vehicle-photo lifecycle bugs ([cf76831](https://github.com/lukislp/ObdGarage/commit/cf768311733640ae6468cf1bbd340adbbbcc34a2))


### Features

* add a Diagnose tab for reading DTCs in the Web UI ([edc919c](https://github.com/lukislp/ObdGarage/commit/edc919cee941cdfc6329f753865baa4fc5a02b72))
* implement EF Core/SQLite persistence backend ([640d4cb](https://github.com/lukislp/ObdGarage/commit/640d4cb15e1af2ceefd9766749ac896799919c08)), closes [hi#severity](https://github.com/hi/issues/severity)
* read diagnostic trouble codes (DTCs) ([7e824fe](https://github.com/lukislp/ObdGarage/commit/7e824fee5654530c869759448cfd2cbcc8df1b27))
* switch Web/Server/App persistence to EF Core/SQLite ([6c16b3a](https://github.com/lukislp/ObdGarage/commit/6c16b3aa9a696f3c9fedf2fe9a1b9ff092d93d3d))

# 1.0.0 (2026-08-06)


### Features

* add Docker images and multi-arch CI/CD pipeline ([6169cc8](https://github.com/lukislp/ObdGarage/commit/6169cc80939b6986c8fbcb1a63a751dddf9f43d7))
