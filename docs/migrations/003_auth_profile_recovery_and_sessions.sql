-- Wersja: 003
-- Cel: rozbudowa kont użytkowników (display_name, opcjonalny zweryfikowany email, auth_provider, token_version)
-- oraz tabele tokenów odświeżania sesji, resetu hasła i weryfikacji adresu e-mail.
-- Bezpieczne do wielokrotnego uruchomienia na istniejącej bazie MariaDB 10.4+.

ALTER TABLE `users`
    ADD COLUMN IF NOT EXISTS `display_name` varchar(80) DEFAULT NULL AFTER `user_password`,
    ADD COLUMN IF NOT EXISTS `email` varchar(254) DEFAULT NULL AFTER `display_name`,
    ADD COLUMN IF NOT EXISTS `email_verified_at` datetime DEFAULT NULL AFTER `email`,
    ADD COLUMN IF NOT EXISTS `auth_provider` enum('local','ldap') NOT NULL DEFAULT 'local' AFTER `email_verified_at`,
    ADD COLUMN IF NOT EXISTS `token_version` int(11) NOT NULL DEFAULT 1 AFTER `auth_provider`;

SET @has_verified_email_col := (
    SELECT COUNT(*)
    FROM `information_schema`.`COLUMNS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'users'
      AND `COLUMN_NAME` = 'verified_email'
);
SET @add_verified_email_col := IF(
    @has_verified_email_col = 0,
    'ALTER TABLE `users` ADD COLUMN `verified_email` varchar(254) GENERATED ALWAYS AS (CASE WHEN `email_verified_at` IS NOT NULL THEN LOWER(`email`) ELSE NULL END) PERSISTENT AFTER `email_verified_at`',
    'SELECT 1'
);
PREPARE stmt_add_verified_col FROM @add_verified_email_col;
EXECUTE stmt_add_verified_col;
DEALLOCATE PREPARE stmt_add_verified_col;

SET @has_verified_email_idx := (
    SELECT COUNT(*)
    FROM `information_schema`.`STATISTICS`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'users'
      AND `INDEX_NAME` = 'uniq_users_verified_email'
);
SET @add_verified_email_idx := IF(
    @has_verified_email_idx = 0,
    'ALTER TABLE `users` ADD UNIQUE KEY `uniq_users_verified_email` (`verified_email`)',
    'SELECT 1'
);
PREPARE stmt_add_verified_idx FROM @add_verified_email_idx;
EXECUTE stmt_add_verified_idx;
DEALLOCATE PREPARE stmt_add_verified_idx;

CREATE TABLE IF NOT EXISTS `refresh_tokens` (
    `id` int(11) NOT NULL AUTO_INCREMENT,
    `user_id` int(11) NOT NULL,
    `token_hash` char(64) NOT NULL,
    `family_id` char(36) NOT NULL,
    `expires_at` datetime NOT NULL,
    `revoked_at` datetime DEFAULT NULL,
    `replaced_by_id` int(11) DEFAULT NULL,
    `created_at` datetime NOT NULL DEFAULT current_timestamp(),
    PRIMARY KEY (`id`),
    UNIQUE KEY `uniq_refresh_token_hash` (`token_hash`),
    KEY `idx_refresh_tokens_user` (`user_id`),
    KEY `idx_refresh_tokens_family` (`family_id`),
    CONSTRAINT `fk_refresh_tokens_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

CREATE TABLE IF NOT EXISTS `password_reset_tokens` (
    `id` int(11) NOT NULL AUTO_INCREMENT,
    `user_id` int(11) NOT NULL,
    `token_hash` char(64) NOT NULL,
    `expires_at` datetime NOT NULL,
    `used_at` datetime DEFAULT NULL,
    `created_at` datetime NOT NULL DEFAULT current_timestamp(),
    PRIMARY KEY (`id`),
    UNIQUE KEY `uniq_password_reset_token_hash` (`token_hash`),
    KEY `idx_password_reset_user` (`user_id`),
    CONSTRAINT `fk_password_reset_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

CREATE TABLE IF NOT EXISTS `email_verification_tokens` (
    `id` int(11) NOT NULL AUTO_INCREMENT,
    `user_id` int(11) NOT NULL,
    `email` varchar(254) NOT NULL,
    `token_hash` char(64) NOT NULL,
    `expires_at` datetime NOT NULL,
    `used_at` datetime DEFAULT NULL,
    `created_at` datetime NOT NULL DEFAULT current_timestamp(),
    PRIMARY KEY (`id`),
    UNIQUE KEY `uniq_email_verification_token_hash` (`token_hash`),
    KEY `idx_email_verification_user` (`user_id`),
    CONSTRAINT `fk_email_verification_user` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;
