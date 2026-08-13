-- 策略 schema v2：只扩展管理员批准的非秘密元数据。
-- 个人 URL、路径、凭据、连接结果和文章内容仍禁止进入 D1。
ALTER TABLE integration_policies
  ADD COLUMN trusted_private_endpoints_json TEXT NOT NULL DEFAULT '[]'
    CHECK(length(trusted_private_endpoints_json) BETWEEN 2 AND 8192
      AND json_valid(trusted_private_endpoints_json)
      AND json_type(trusted_private_endpoints_json) = 'array');

ALTER TABLE integration_policies
  ADD COLUMN allowed_resources_json TEXT NOT NULL DEFAULT '[]'
    CHECK(length(allowed_resources_json) BETWEEN 2 AND 8192
      AND json_valid(allowed_resources_json)
      AND json_type(allowed_resources_json) = 'array');

ALTER TABLE integration_policies
  ADD COLUMN allowed_loopback_http_ports_json TEXT NOT NULL DEFAULT '[]'
    CHECK(length(allowed_loopback_http_ports_json) BETWEEN 2 AND 8192
      AND json_valid(allowed_loopback_http_ports_json)
      AND json_type(allowed_loopback_http_ports_json) = 'array');
