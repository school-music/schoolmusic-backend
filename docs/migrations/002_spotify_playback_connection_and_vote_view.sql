-- Wersja: 002
-- Cel: dodanie brakującej tabeli spotify_playback_connection oraz bezpieczna naprawa widoku song_vote_counts
-- (usunięcie ewentualnej tabeli tymczasowej placeholder i utworzenie widoku jako SQL SECURITY INVOKER).
-- Bezpieczne do wielokrotnego uruchomienia na istniejącej bazie.

CREATE TABLE IF NOT EXISTS `spotify_playback_connection` (
    `id` int(11) NOT NULL,
    `spotify_user_id` varchar(128) NOT NULL,
    `refresh_token_ciphertext` text NOT NULL,
    `refresh_token_expires_at` datetime NOT NULL,
    `created_at` datetime NOT NULL,
    `updated_at` datetime NOT NULL,
    PRIMARY KEY (`id`),
    CONSTRAINT `chk_spotify_playback_singleton` CHECK (`id` = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_polish_ci;

SET @song_vote_counts_is_table := (
    SELECT COUNT(*)
    FROM `information_schema`.`TABLES`
    WHERE `TABLE_SCHEMA` = DATABASE()
      AND `TABLE_NAME` = 'song_vote_counts'
      AND `TABLE_TYPE` = 'BASE TABLE'
);
SET @drop_placeholder_sql := IF(@song_vote_counts_is_table > 0, 'DROP TABLE `song_vote_counts`', 'SELECT 1');
PREPARE stmt_drop_placeholder FROM @drop_placeholder_sql;
EXECUTE stmt_drop_placeholder;
DEALLOCATE PREPARE stmt_drop_placeholder;

DROP VIEW IF EXISTS `song_vote_counts`;

CREATE ALGORITHM=UNDEFINED SQL SECURITY INVOKER VIEW `song_vote_counts` AS
SELECT
    `s`.`id` AS `song_id`,
    `s`.`title` AS `title`,
    COUNT(`v`.`id`) AS `total_votes`
FROM `songs` `s`
LEFT JOIN `queue_items` `qi` ON `qi`.`song_id` = `s`.`id`
LEFT JOIN `votes` `v` ON `v`.`queue_item_id` = `qi`.`id`
WHERE `s`.`is_active` = 1
GROUP BY `s`.`id`, `s`.`title`;
