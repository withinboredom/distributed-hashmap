FROM php:8-cli
COPY --from=mlocati/php-extension-installer /usr/bin/install-php-extensions /usr/local/bin/
RUN apt-get update && apt-get install -y wget git unzip && apt-get clean && rm -rf /var/cache/apt/lists
RUN install-php-extensions curl intl pcntl posix zip sodium @composer && mkdir -p /app
WORKDIR /app
COPY composer.json composer.json
COPY composer.lock composer.lock
RUN composer install --no-dev -n -o
COPY reference reference
COPY tests tests
ENTRYPOINT ["php","tests/php/integration.php"]
