# Changelog

## 1.0.0 (2026-08-09)


### Features

* add materialized views and schema scaffolding ([6a9e495](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/6a9e4957fb653777c41c32a37453908b62965e38))
* add options, session management and the provider service graph ([775cd7b](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/775cd7bfdb516686cb0462c31f32bef45ff02c13))
* add PER PARTITION LIMIT ([95f185b](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/95f185b0fb9c68266d0dcf2c8a3ea29aafb1db5c))
* add projections, cursor paging and secondary indexes ([c68bdb6](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/c68bdb688fc21129503322cff3347b1cfef407de))
* add the telemetry activity source and meter ([bec78a0](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/bec78a04f46f288251cd4c53e41039b83860282a))
* add time-to-live, write timestamps and lightweight transactions ([3c2b5c3](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/3c2b5c30288b804f0e49564563b653b64562a4c6))
* emit a trace span for every CQL statement ([e884650](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/e8846507031e20a0626ae9cddf656d1321d0de41))
* map models to CQL schema and create it ([69fdf81](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/69fdf819670a3587f4c0d1842764715e5f57b940))
* require a conditional insert alongside a concurrency token ([8e2781c](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/8e2781c987b0f66805def18a8b1bafcea2a85acd))
* translate LINQ queries to CQL ([4347d91](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/4347d91abac4647ed4891870335aa8848994ea0e))
* write tracked changes as CQL ([2d0293d](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/2d0293df224e462ed27eecc1080198b253956725))


### Bug Fixes

* address final code review findings on observability feature ([48d4cf9](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/48d4cf971b17fc9bba8f70e1308ee4ab5b9d4f77))
* close residual observability review findings ([1d71674](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/1d7167444d85169670288b0fbe87409299657371))
* isolate telemetry from query outcome and preserve operation name ([e1eff84](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/commit/e1eff844098157368a83d7fba9d3e5717be282f7))
