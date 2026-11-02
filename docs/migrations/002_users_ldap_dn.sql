-- Wersja: 002
-- Cel: powiązanie kont lokalnych ze szkolnym katalogiem LDAP.
-- Bezpieczne do wielokrotnego uruchomienia.

SET @column_exists := (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'users'
      AND COLUMN_NAME = 'ldap_dn'
);

SET @sql := IF(
    @column_exists = 0,
    'ALTER TABLE `users` ADD COLUMN `ldap_dn` VARCHAR(512) NULL AFTER `password_changed_at`',
    'DO 0'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @index_exists := (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'users'
      AND INDEX_NAME = 'uniq_users_ldap_dn'
);

SET @sql := IF(
    @index_exists = 0,
    'ALTER TABLE `users` ADD UNIQUE KEY `uniq_users_ldap_dn` (`ldap_dn`)',
    'DO 0'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
