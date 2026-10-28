-- Wersja: 001
-- Cel: usunięcie zależności widoku od konta root i uprawnień DEFINER.
-- Bezpieczne do wielokrotnego uruchomienia na istniejącej bazie.

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
